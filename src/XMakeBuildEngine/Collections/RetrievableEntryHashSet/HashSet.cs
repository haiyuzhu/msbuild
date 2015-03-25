﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
#if !SILVERLIGHT
using System.Runtime.Serialization;
#endif
using System.Security.Permissions;
using System.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security;
#if SILVERLIGHT
using System.Core; // for System.Core.SR
#endif
using Microsoft.Build.Shared;
using Microsoft.Build.Internal;

/*
    ==================================================================================================================
    MSBUILD COMMENT:

    Ripped off from Hashset.cs with the following changes:

    * class renamed
    * unnecessary methods and attributes if-deffed out (code retained to help windiff, but indented)
    * require T implements IKeyed, and accept IKeyed directly where necessary
    * all constructors require a comparer -- an IEqualityComparer<IKeyed> -- to avoid mistakes
    * change Contains to give you back the found entry, rather than a boolean
    * change Add so that it always adds, even if there's an entry already present with the same name. 
           We want "replacement" semantics, like a dictionary keyed on name.
    * constructor that allows the collection to be read-only
    * implement IDictionary<string, T>
    * some convenience methods taking 'string' as overloads of methods taking IKeyed
    
    Other than this it is modified absolutely minimally to make it easy to diff with the originals (in the Originals folder) 
    to verify that no errors were introduced, and make it easier to possibly pick up any future bug fixes to the original. 
    The care taken to minimally modify this means that it is not necessary to carefully code review this complex class, 
    nor unit test it directly.
    ==================================================================================================================
*/

namespace Microsoft.Build.Collections
{
    /// <summary>
    /// Implementation notes:
    /// This uses an array-based implementation similar to Dictionary<T>, using a buckets array
    /// to map hash values to the Slots array. Items in the Slots array that hash to the same value
    /// are chained together through the "next" indices. 
    /// 
    /// The capacity is always prime; so during resizing, the capacity is chosen as the next prime
    /// greater than double the last capacity. 
    /// 
    /// The underlying data structures are lazily initialized. Because of the observation that, 
    /// in practice, hashtables tend to contain only a few elements, the initial capacity is
    /// set very small (3 elements) unless the ctor with a collection is used.
    /// 
    /// The +/- 1 modifications in methods that add, check for containment, etc allow us to 
    /// distinguish a hash code of 0 from an uninitialized bucket. This saves us from having to 
    /// reset each bucket to -1 when resizing. See Contains, for example.
    /// 
    /// Set methods such as UnionWith, IntersectWith, ExceptWith, and SymmetricExceptWith modify
    /// this set.
    /// 
    /// Some operations can perform faster if we can assume "other" contains unique elements
    /// according to this equality comparer. The only times this is efficient to check is if
    /// other is a hashset. Note that checking that it's a hashset alone doesn't suffice; we
    /// also have to check that the hashset is using the same equality comparer. If other 
    /// has a different equality comparer, it will have unique elements according to its own
    /// equality comparer, but not necessarily according to ours. Therefore, to go these 
    /// optimized routes we check that other is a hashset using the same equality comparer.
    /// 
    /// A HashSet with no elements has the properties of the empty set. (See IsSubset, etc. for 
    /// special empty set checks.)
    /// 
    /// A couple of methods have a special case if other is this (e.g. SymmetricExceptWith). 
    /// If we didn't have these checks, we could be iterating over the set and modifying at
    /// the same time. 
    /// </summary>
    /// <typeparam name="T"></typeparam>
    [DebuggerTypeProxy(typeof(Microsoft.Build.Collections.HashSetDebugView<>))]
    [DebuggerDisplay("Count = {Count}")]
    [SuppressMessage("Microsoft.Naming", "CA1710:IdentifiersShouldHaveCorrectSuffix", Justification = "By design")]
#if SILVERLIGHT
    public class HashSet<T> : ICollection<T>, ISet<T>
#else
    [Serializable()]
#if !MONO
    [System.Security.Permissions.HostProtection(MayLeakOnAbort = true)]
#endif
    internal class RetrievableEntryHashSet<T> : ICollection<T>, ISerializable, IDeserializationCallback, IDictionary<string, T>
        where T : class, IKeyed
#endif
    {
        // store lower 31 bits of hash code
        private const int Lower31BitMask = 0x7FFFFFFF;
        // cutoff point, above which we won't do stackallocs. This corresponds to 100 integers.
        private const int StackAllocThreshold = 100;
        // when constructing a hashset from an existing collection, it may contain duplicates, 
        // so this is used as the max acceptable excess ratio of capacity to count. Note that
        // this is only used on the ctor and not to automatically shrink if the hashset has, e.g,
        // a lot of adds followed by removes. Users must explicitly shrink by calling TrimExcess.
        // This is set to 3 because capacity is acceptable as 2x rounded up to nearest prime.
        private const int ShrinkThreshold = 3;

#if !SILVERLIGHT
        // constants for serialization
        private const String CapacityName = "Capacity";
        private const String ElementsName = "Elements";
        private const String ComparerName = "Comparer";
        private const String VersionName = "Version";
#endif

        private int[] _buckets;
        private Slot[] _slots;
        private int _count;
        private int _lastIndex;
        private int _freeList;
        private IEqualityComparer<IKeyed> _comparer;
        private int _version;
        private bool _readOnly;

#if !SILVERLIGHT
        // temporary variable needed during deserialization
        private SerializationInfo _siInfo;
#endif

        #region Constructors

        public RetrievableEntryHashSet(IEqualityComparer<IKeyed> comparer)
        {
            if (comparer == null)
            {
                ErrorUtilities.ThrowInternalError("use explicit comparer");
            }

            _comparer = comparer;
            _lastIndex = 0;
            _count = 0;
            _freeList = -1;
            _version = 0;
        }

        public RetrievableEntryHashSet(IEnumerable<T> collection, IEqualityComparer<IKeyed> comparer, bool readOnly = false)
            : this(collection, comparer)
        {
            _readOnly = true; // Set after possible initialization from another collection
        }

        public RetrievableEntryHashSet(IEnumerable<KeyValuePair<string, T>> collection, IEqualityComparer<IKeyed> comparer, bool readOnly = false)
            : this(collection.Values(), comparer, readOnly)
        {
            _readOnly = true; // Set after possible initialization from another collection
        }

        /// <summary>
        /// Implementation Notes:
        /// Since resizes are relatively expensive (require rehashing), this attempts to minimize 
        /// the need to resize by setting the initial capacity based on size of collection. 
        /// </summary>
        /// <param name="collection"></param>
        /// <param name="comparer"></param>
        public RetrievableEntryHashSet(int suggestedCapacity, IEqualityComparer<IKeyed> comparer)
            : this(comparer)
        {
            Initialize(suggestedCapacity);
        }

        /// <summary>
        /// Implementation Notes:
        /// Since resizes are relatively expensive (require rehashing), this attempts to minimize 
        /// the need to resize by setting the initial capacity based on size of collection. 
        /// </summary>
        /// <param name="collection"></param>
        /// <param name="comparer"></param>
        public RetrievableEntryHashSet(IEnumerable<T> collection, IEqualityComparer<IKeyed> comparer)
            : this(comparer)
        {
            if (collection == null)
            {
                throw new ArgumentNullException("collection");
            }

            Contract.EndContractBlock();

            // to avoid excess resizes, first set size based on collection's count. Collection
            // may contain duplicates, so call TrimExcess if resulting hashset is larger than
            // threshold
            int suggestedCapacity = 0;
            ICollection<T> coll = collection as ICollection<T>;
            if (coll != null)
            {
                suggestedCapacity = coll.Count;
            }
            Initialize(suggestedCapacity);

            this.UnionWith(collection);
            if ((_count == 0 && _slots.Length > HashHelpers.GetMinPrime()) ||
                (_count > 0 && _slots.Length / _count > ShrinkThreshold))
            {
                TrimExcess();
            }
        }

#if !SILVERLIGHT
        protected RetrievableEntryHashSet(SerializationInfo info, StreamingContext context)
        {
            // We can't do anything with the keys and values until the entire graph has been 
            // deserialized and we have a reasonable estimate that GetHashCode is not going to 
            // fail.  For the time being, we'll just cache this.  The graph is not valid until 
            // OnDeserialization has been called.
            _siInfo = info;
        }
#endif

        #endregion

        // Convenience to minimise change to callers used to dictionaries
        public ICollection<string> Keys
        {
            get
            {
                return new ReadOnlyConvertingCollection<T, string>(this, delegate (T input) { return input.Key; }, delegate (string key) { return Contains(key); });
            }
        }

        // Convenience to minimise change to callers used to dictionaries
        public ICollection<T> Values
        {
            get { return this; }
        }

        #region ICollection<T> methods

        // Convenience to minimise change to callers used to dictionaries
        internal T this[string name]
        {
            get
            {
                return Get(name);
            }

            set
            {
                Debug.Assert(String.Equals(name, value.Key, StringComparison.Ordinal));
                Add(value);
            }
        }

        /// <summary>
        /// Add item to this hashset. This is the explicit implementation of the ICollection<T>
        /// interface. The other Add method returns bool indicating whether item was added.
        /// </summary>
        /// <param name="item">item to add</param>
        void ICollection<T>.Add(T item)
        {
            AddEvenIfPresent(item);
        }

        /// <summary>
        /// Remove all items from this set. This clears the elements but not the underlying 
        /// buckets and slots array. Follow this call by TrimExcess to release these.
        /// </summary>
        public void Clear()
        {
            if (_readOnly)
            {
                ErrorUtilities.ThrowInvalidOperation("OM_NotSupportedReadOnlyCollection");
            }

            if (_lastIndex > 0)
            {
                Debug.Assert(_buckets != null, "m_buckets was null but m_lastIndex > 0");

                // clear the elements so that the gc can reclaim the references.
                // clear only up to m_lastIndex for m_slots 
                Array.Clear(_slots, 0, _lastIndex);
                Array.Clear(_buckets, 0, _buckets.Length);
                _lastIndex = 0;
                _count = 0;
                _freeList = -1;
            }
            _version++;
        }

        // Convenience
        internal bool Contains(string key)
        {
            return (Get(key) != null);
        }

        bool ICollection<KeyValuePair<string, T>>.Contains(KeyValuePair<string, T> entry)
        {
            Debug.Assert(String.Equals(entry.Key, entry.Value.Key, StringComparison.Ordinal));
            return (Get(entry.Value) != null);
        }

        public bool ContainsKey(string key)
        {
            return (Get(key) != null);
        }

        T IDictionary<string, T>.this[string name]
        {
            get { return Get(name); }
            set { Add(value); }
        }

        /// <summary>
        /// Checks if this hashset contains the item
        /// </summary>
        /// <param name="item">item to check for containment</param>
        /// <returns>true if item contained; false if not</returns>
        public bool Contains(T item)
        {
            return (Get(item.Key) != null);
        }

        // Convenience to minimise change to callers used to dictionaries
        public bool TryGetValue(string key, out T item)
        {
            item = Get(key);
            return (item != null);
        }

        /// <summary>
        /// Gets the item if any with the given name
        /// </summary>
        /// <param name="item">item to check for containment</param>
        /// <returns>true if item contained; false if not</returns>
        public T Get(string key)
        {
            return Get(new KeyedObject(key));
        }

        /// <summary>
        /// Gets the item if any with the given name
        /// </summary>
        /// <param name="item">item to check for containment</param>
        /// <returns>true if item contained; false if not</returns>
        public T Get(IKeyed item)
        {
            if (_buckets != null)
            {
                int hashCode = InternalGetHashCode(item);
                // see note at "HashSet" level describing why "- 1" appears in for loop
                for (int i = _buckets[hashCode % _buckets.Length] - 1; i >= 0; i = _slots[i].next)
                {
                    if (_slots[i].hashCode == hashCode && _comparer.Equals(_slots[i].value, item))
                    {
                        return _slots[i].value;
                    }
                }
            }
            // either m_buckets is null or wasn't found
            return default(T);
        }

        /// <summary>
        /// Copy items in this hashset to array, starting at arrayIndex
        /// </summary>
        /// <param name="array">array to add items to</param>
        /// <param name="arrayIndex">index to start at</param>
        public void CopyTo(T[] array, int arrayIndex)
        {
            CopyTo(array, arrayIndex, _count);
        }

        /// <summary>
        /// Remove by key
        /// </summary>
        public bool Remove(string item)
        {
            return Remove(new KeyedObject(item));
        }

        /// <summary>
        /// Remove entry that compares equal to T
        /// </summary>        
        public bool Remove(T item)
        {
            return Remove((IKeyed)item);
        }

        bool ICollection<KeyValuePair<string, T>>.Remove(KeyValuePair<string, T> entry)
        {
            Debug.Assert(String.Equals(entry.Key, entry.Value.Key, StringComparison.Ordinal));
            return Remove(entry.Value);
        }

        /// <summary>
        /// Remove item from this hashset
        /// </summary>
        /// <param name="item">item to remove</param>
        /// <returns>true if removed; false if not (i.e. if the item wasn't in the HashSet)</returns>
        private bool Remove(IKeyed item)
        {
            if (_readOnly)
            {
                ErrorUtilities.ThrowInvalidOperation("OM_NotSupportedReadOnlyCollection");
            }

            if (_buckets != null)
            {
                int hashCode = InternalGetHashCode(item);
                int bucket = hashCode % _buckets.Length;
                int last = -1;
                for (int i = _buckets[bucket] - 1; i >= 0; last = i, i = _slots[i].next)
                {
                    if (_slots[i].hashCode == hashCode && _comparer.Equals(_slots[i].value, item))
                    {
                        if (last < 0)
                        {
                            // first iteration; update buckets
                            _buckets[bucket] = _slots[i].next + 1;
                        }
                        else
                        {
                            // subsequent iterations; update 'next' pointers
                            _slots[last].next = _slots[i].next;
                        }
                        _slots[i].hashCode = -1;
                        _slots[i].value = default(T);
                        _slots[i].next = _freeList;

                        _count--;
                        _version++;
                        if (_count == 0)
                        {
                            _lastIndex = 0;
                            _freeList = -1;
                        }
                        else
                        {
                            _freeList = i;
                        }
                        return true;
                    }
                }
            }
            // either m_buckets is null or wasn't found
            return false;
        }

        /// <summary>
        /// Number of elements in this hashset
        /// </summary>
        public int Count
        {
            get { return _count; }
        }

        /// <summary>
        /// Whether this is readonly
        /// </summary>
        public bool IsReadOnly
        {
            get { return _readOnly; }
        }

        /// <summary>
        /// Permanently prevent changes to the set.
        /// </summary>
        internal void MakeReadOnly()
        {
            _readOnly = true;
        }

        #endregion

        #region IEnumerable methods

        public Enumerator GetEnumerator()
        {
            return new Enumerator(this);
        }

        IEnumerator<KeyValuePair<string, T>> IEnumerable<KeyValuePair<string, T>>.GetEnumerator()
        {
            foreach (var entry in this)
            {
                yield return new KeyValuePair<string, T>(entry.Key, entry);
            }
        }

        IEnumerator<T> IEnumerable<T>.GetEnumerator()
        {
            return new Enumerator(this);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return new Enumerator(this);
        }

        #endregion

        #region ISerializable methods

#if !SILVERLIGHT
        // [SecurityPermissionAttribute(SecurityAction.LinkDemand, Flags = SecurityPermissionFlag.SerializationFormatter)]
        [SecurityCritical]
        public virtual void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            if (info == null)
            {
                throw new ArgumentNullException("info");
            }

            // need to serialize version to avoid problems with serializing while enumerating
            info.AddValue(VersionName, _version);
            info.AddValue(ComparerName, _comparer, typeof(IEqualityComparer<T>));
            info.AddValue(CapacityName, _buckets == null ? 0 : _buckets.Length);
            if (_buckets != null)
            {
                T[] array = new T[_count];
                CopyTo(array);
                info.AddValue(ElementsName, array, typeof(T[]));
            }
        }
#endif
        #endregion

        #region IDeserializationCallback methods

#if !SILVERLIGHT
        public virtual void OnDeserialization(Object sender)
        {
            if (_siInfo == null)
            {
                // It might be necessary to call OnDeserialization from a container if the 
                // container object also implements OnDeserialization. However, remoting will 
                // call OnDeserialization again. We can return immediately if this function is 
                // called twice. Note we set m_siInfo to null at the end of this method.
                return;
            }

            int capacity = _siInfo.GetInt32(CapacityName);
            _comparer = (IEqualityComparer<IKeyed>)_siInfo.GetValue(ComparerName, typeof(IEqualityComparer<IKeyed>));
            _freeList = -1;

            if (capacity != 0)
            {
                _buckets = new int[capacity];
                _slots = new Slot[capacity];

                T[] array = (T[])_siInfo.GetValue(ElementsName, typeof(T[]));

                if (array == null)
                {
                    throw new SerializationException();
                }

                // there are no resizes here because we already set capacity above
                for (int i = 0; i < array.Length; i++)
                {
                    AddEvenIfPresent(array[i]);
                }
            }
            else
            {
                _buckets = null;
            }

            _version = _siInfo.GetInt32(VersionName);
            _siInfo = null;
        }
#endif

        #endregion

        #region HashSet methods

        /// <summary>
        /// Add item to this HashSet. 
        /// *** MSBUILD NOTE: Always added - overwrite semantics
        /// </summary>
        public void Add(T item)
        {
            AddEvenIfPresent(item);
        }

        void IDictionary<string, T>.Add(string key, T item)
        {
            if (key != item.Key)
                throw new InvalidOperationException();

            AddEvenIfPresent(item);
        }

        void ICollection<KeyValuePair<string, T>>.Add(KeyValuePair<string, T> entry)
        {
            Debug.Assert(String.Equals(entry.Key, entry.Value.Key, StringComparison.Ordinal));

            AddEvenIfPresent(entry.Value);
        }

        /// <summary>
        /// Take the union of this HashSet with other. Modifies this set.
        /// 
        /// Implementation note: GetSuggestedCapacity (to increase capacity in advance avoiding 
        /// multiple resizes ended up not being useful in practice; quickly gets to the 
        /// point where it's a wasteful check.
        /// </summary>
        /// <param name="other">enumerable with items to add</param>
        public void UnionWith(IEnumerable<T> other)
        {
            if (other == null)
            {
                throw new ArgumentNullException("other");
            }
            Contract.EndContractBlock();

            foreach (T item in other)
            {
                AddEvenIfPresent(item);
            }
        }

#if NEVER 
                                                                                                                                                        /// <summary>
                                                                                                                                                        /// Takes the intersection of this set with other. Modifies this set.
                                                                                                                                                        /// 
                                                                                                                                                        /// Implementation Notes: 
                                                                                                                                                        /// We get better perf if other is a hashset using same equality comparer, because we 
                                                                                                                                                        /// get constant contains check in other. Resulting cost is O(n1) to iterate over this.
                                                                                                                                                        /// 
                                                                                                                                                        /// If we can't go above route, iterate over the other and mark intersection by checking
                                                                                                                                                        /// contains in this. Then loop over and delete any unmarked elements. Total cost is n2+n1. 
                                                                                                                                                        /// 
                                                                                                                                                        /// Attempts to return early based on counts alone, using the property that the 
                                                                                                                                                        /// intersection of anything with the empty set is the empty set.
                                                                                                                                                        /// </summary>
                                                                                                                                                        /// <param name="other">enumerable with items to add </param>
                                                                                                                                                        public void IntersectWith(IEnumerable<T> other) {
                                                                                                                                                            if (other == null) {
                                                                                                                                                                throw new ArgumentNullException("other");
                                                                                                                                                            }
                                                                                                                                                            Contract.EndContractBlock();

                                                                                                                                                            // intersection of anything with empty set is empty set, so return if count is 0
                                                                                                                                                            if (m_count == 0) {
                                                                                                                                                                return;
                                                                                                                                                            }

                                                                                                                                                            // if other is empty, intersection is empty set; remove all elements and we're done
                                                                                                                                                            // can only figure this out if implements ICollection<T>. (IEnumerable<T> has no count)
                                                                                                                                                            ICollection<T> otherAsCollection = other as ICollection<T>;
                                                                                                                                                            if (otherAsCollection != null) {
                                                                                                                                                                if (otherAsCollection.Count == 0) {
                                                                                                                                                                    Clear();
                                                                                                                                                                    return;
                                                                                                                                                                }

                                                                                                                                                                RetrievableEntryHashSet<T> otherAsSet = other as RetrievableEntryHashSet<T>;
                                                                                                                                                                // faster if other is a hashset using same equality comparer; so check 
                                                                                                                                                                // that other is a hashset using the same equality comparer.
                                                                                                                                                                if (otherAsSet != null && AreEqualityComparersEqual(this, otherAsSet)) {
                                                                                                                                                                    IntersectWithHashSetWithSameEC(otherAsSet);
                                                                                                                                                                    return;
                                                                                                                                                                }
                                                                                                                                                            }

                                                                                                                                                            IntersectWithEnumerable(other);
                                                                                                                                                        }

                                                                                                                                                        /// <summary>
                                                                                                                                                        /// Remove items in other from this set. Modifies this set.
                                                                                                                                                        /// </summary>
                                                                                                                                                        /// <param name="other">enumerable with items to remove</param>
                                                                                                                                                        public void ExceptWith(IEnumerable<T> other) {
                                                                                                                                                            if (other == null) {
                                                                                                                                                                throw new ArgumentNullException("other");
                                                                                                                                                            }
                                                                                                                                                            Contract.EndContractBlock();

                                                                                                                                                            // this is already the enpty set; return
                                                                                                                                                            if (m_count == 0) {
                                                                                                                                                                return;
                                                                                                                                                            }

                                                                                                                                                            // special case if other is this; a set minus itself is the empty set
                                                                                                                                                            if (other == this) {
                                                                                                                                                                Clear();
                                                                                                                                                                return;
                                                                                                                                                            }

                                                                                                                                                            // remove every element in other from this
                                                                                                                                                            foreach (T element in other) {
                                                                                                                                                                Remove(element);
                                                                                                                                                            }
                                                                                                                                                        }


                                                                                                                                                        /// <summary>
                                                                                                                                                        /// Takes symmetric difference (XOR) with other and this set. Modifies this set.
                                                                                                                                                        /// </summary>
                                                                                                                                                        /// <param name="other">enumerable with items to XOR</param>
                                                                                                                                                        public void SymmetricExceptWith(IEnumerable<T> other) {
                                                                                                                                                            if (other == null) {
                                                                                                                                                                throw new ArgumentNullException("other");
                                                                                                                                                            }
                                                                                                                                                            Contract.EndContractBlock();

                                                                                                                                                            // if set is empty, then symmetric difference is other
                                                                                                                                                            if (m_count == 0) {
                                                                                                                                                                UnionWith(other);
                                                                                                                                                                return;
                                                                                                                                                            }

                                                                                                                                                            // special case this; the symmetric difference of a set with itself is the empty set
                                                                                                                                                            if (other == this) {
                                                                                                                                                                Clear();
                                                                                                                                                                return;
                                                                                                                                                            }

                                                                                                                                                            RetrievableEntryHashSet<T> otherAsSet = other as RetrievableEntryHashSet<T>;
                                                                                                                                                            // If other is a HashSet, it has unique elements according to its equality comparer,
                                                                                                                                                            // but if they're using different equality comparers, then assumption of uniqueness
                                                                                                                                                            // will fail. So first check if other is a hashset using the same equality comparer;
                                                                                                                                                            // symmetric except is a lot faster and avoids bit array allocations if we can assume
                                                                                                                                                            // uniqueness
                                                                                                                                                            if (otherAsSet != null && AreEqualityComparersEqual(this, otherAsSet)) {
                                                                                                                                                                SymmetricExceptWithUniqueHashSet(otherAsSet);
                                                                                                                                                            }
                                                                                                                                                            else {
                                                                                                                                                                SymmetricExceptWithEnumerable(other);
                                                                                                                                                            }
                                                                                                                                                        }

                                                                                                                                                        /// <summary>
                                                                                                                                                        /// Checks if this is a subset of other.
                                                                                                                                                        /// 
                                                                                                                                                        /// Implementation Notes:
                                                                                                                                                        /// The following properties are used up-front to avoid element-wise checks:
                                                                                                                                                        /// 1. If this is the empty set, then it's a subset of anything, including the empty set
                                                                                                                                                        /// 2. If other has unique elements according to this equality comparer, and this has more
                                                                                                                                                        /// elements than other, then it can't be a subset.
                                                                                                                                                        /// 
                                                                                                                                                        /// Furthermore, if other is a hashset using the same equality comparer, we can use a 
                                                                                                                                                        /// faster element-wise check.
                                                                                                                                                        /// </summary>
                                                                                                                                                        /// <param name="other"></param>
                                                                                                                                                        /// <returns>true if this is a subset of other; false if not</returns>
                                                                                                                                                        public bool IsSubsetOf(IEnumerable<T> other) {
                                                                                                                                                            if (other == null) {
                                                                                                                                                                throw new ArgumentNullException("other");
                                                                                                                                                            }
                                                                                                                                                            Contract.EndContractBlock();

                                                                                                                                                            // The empty set is a subset of any set
                                                                                                                                                            if (m_count == 0) {
                                                                                                                                                                return true;
                                                                                                                                                            }

                                                                                                                                                            RetrievableEntryHashSet<T> otherAsSet = other as RetrievableEntryHashSet<T>;
                                                                                                                                                            // faster if other has unique elements according to this equality comparer; so check 
                                                                                                                                                            // that other is a hashset using the same equality comparer.
                                                                                                                                                            if (otherAsSet != null && AreEqualityComparersEqual(this, otherAsSet)) {
                                                                                                                                                                // if this has more elements then it can't be a subset
                                                                                                                                                                if (m_count > otherAsSet.Count) {
                                                                                                                                                                    return false;
                                                                                                                                                                }

                                                                                                                                                                // already checked that we're using same equality comparer. simply check that 
                                                                                                                                                                // each element in this is contained in other.
                                                                                                                                                                return IsSubsetOfHashSetWithSameEC(otherAsSet);
                                                                                                                                                            }
                                                                                                                                                            else {
                                                                                                                                                                ElementCount result = CheckUniqueAndUnfoundElements(other, false);
                                                                                                                                                                return (result.uniqueCount == m_count && result.unfoundCount >= 0);
                                                                                                                                                            }
                                                                                                                                                        }

                                                                                                                                                        /// <summary>
                                                                                                                                                        /// Checks if this is a proper subset of other (i.e. strictly contained in)
                                                                                                                                                        /// 
                                                                                                                                                        /// Implementation Notes:
                                                                                                                                                        /// The following properties are used up-front to avoid element-wise checks:
                                                                                                                                                        /// 1. If this is the empty set, then it's a proper subset of a set that contains at least
                                                                                                                                                        /// one element, but it's not a proper subset of the empty set.
                                                                                                                                                        /// 2. If other has unique elements according to this equality comparer, and this has >=
                                                                                                                                                        /// the number of elements in other, then this can't be a proper subset.
                                                                                                                                                        /// 
                                                                                                                                                        /// Furthermore, if other is a hashset using the same equality comparer, we can use a 
                                                                                                                                                        /// faster element-wise check.
                                                                                                                                                        /// </summary>
                                                                                                                                                        /// <param name="other"></param>
                                                                                                                                                        /// <returns>true if this is a proper subset of other; false if not</returns>
                                                                                                                                                        public bool IsProperSubsetOf(IEnumerable<T> other) {
                                                                                                                                                            if (other == null) {
                                                                                                                                                                throw new ArgumentNullException("other");
                                                                                                                                                            }
                                                                                                                                                            Contract.EndContractBlock();

                                                                                                                                                            ICollection<T> otherAsCollection = other as ICollection<T>;
                                                                                                                                                            if (otherAsCollection != null) {
                                                                                                                                                                // the empty set is a proper subset of anything but the empty set
                                                                                                                                                                if (m_count == 0) {
                                                                                                                                                                    return otherAsCollection.Count > 0;
                                                                                                                                                                }
                                                                                                                                                                RetrievableEntryHashSet<T> otherAsSet = other as RetrievableEntryHashSet<T>;
                                                                                                                                                                // faster if other is a hashset (and we're using same equality comparer)
                                                                                                                                                                if (otherAsSet != null && AreEqualityComparersEqual(this, otherAsSet)) {
                                                                                                                                                                    if (m_count >= otherAsSet.Count) {
                                                                                                                                                                        return false;
                                                                                                                                                                    }
                                                                                                                                                                    // this has strictly less than number of items in other, so the following
                                                                                                                                                                    // check suffices for proper subset.
                                                                                                                                                                    return IsSubsetOfHashSetWithSameEC(otherAsSet);
                                                                                                                                                                }
                                                                                                                                                            }

                                                                                                                                                            ElementCount result = CheckUniqueAndUnfoundElements(other, false);
                                                                                                                                                            return (result.uniqueCount == m_count && result.unfoundCount > 0);

                                                                                                                                                        }

                                                                                                                                                        /// <summary>
                                                                                                                                                        /// Checks if this is a superset of other
                                                                                                                                                        /// 
                                                                                                                                                        /// Implementation Notes:
                                                                                                                                                        /// The following properties are used up-front to avoid element-wise checks:
                                                                                                                                                        /// 1. If other has no elements (it's the empty set), then this is a superset, even if this
                                                                                                                                                        /// is also the empty set.
                                                                                                                                                        /// 2. If other has unique elements according to this equality comparer, and this has less 
                                                                                                                                                        /// than the number of elements in other, then this can't be a superset
                                                                                                                                                        /// 
                                                                                                                                                        /// </summary>
                                                                                                                                                        /// <param name="other"></param>
                                                                                                                                                        /// <returns>true if this is a superset of other; false if not</returns>
                                                                                                                                                        public bool IsSupersetOf(IEnumerable<T> other) {
                                                                                                                                                            if (other == null) {
                                                                                                                                                                throw new ArgumentNullException("other");
                                                                                                                                                            }
                                                                                                                                                            Contract.EndContractBlock();

                                                                                                                                                            // try to fall out early based on counts
                                                                                                                                                            ICollection<T> otherAsCollection = other as ICollection<T>;
                                                                                                                                                            if (otherAsCollection != null) {
                                                                                                                                                                // if other is the empty set then this is a superset
                                                                                                                                                                if (otherAsCollection.Count == 0) {
                                                                                                                                                                    return true;
                                                                                                                                                                }
                                                                                                                                                                RetrievableEntryHashSet<T> otherAsSet = other as RetrievableEntryHashSet<T>;
                                                                                                                                                                // try to compare based on counts alone if other is a hashset with
                                                                                                                                                                // same equality comparer
                                                                                                                                                                if (otherAsSet != null && AreEqualityComparersEqual(this, otherAsSet)) {
                                                                                                                                                                    if (otherAsSet.Count > m_count) {
                                                                                                                                                                        return false;
                                                                                                                                                                    }
                                                                                                                                                                }
                                                                                                                                                            }

                                                                                                                                                            return ContainsAllElements(other);
                                                                                                                                                        }

                                                                                                                                                        /// <summary>
                                                                                                                                                        /// Checks if this is a proper superset of other (i.e. other strictly contained in this)
                                                                                                                                                        /// 
                                                                                                                                                        /// Implementation Notes: 
                                                                                                                                                        /// This is slightly more complicated than above because we have to keep track if there
                                                                                                                                                        /// was at least one element not contained in other.
                                                                                                                                                        /// 
                                                                                                                                                        /// The following properties are used up-front to avoid element-wise checks:
                                                                                                                                                        /// 1. If this is the empty set, then it can't be a proper superset of any set, even if 
                                                                                                                                                        /// other is the empty set.
                                                                                                                                                        /// 2. If other is an empty set and this contains at least 1 element, then this is a proper
                                                                                                                                                        /// superset.
                                                                                                                                                        /// 3. If other has unique elements according to this equality comparer, and other's count
                                                                                                                                                        /// is greater than or equal to this count, then this can't be a proper superset
                                                                                                                                                        /// 
                                                                                                                                                        /// Furthermore, if other has unique elements according to this equality comparer, we can
                                                                                                                                                        /// use a faster element-wise check.
                                                                                                                                                        /// </summary>
                                                                                                                                                        /// <param name="other"></param>
                                                                                                                                                        /// <returns>true if this is a proper superset of other; false if not</returns>
                                                                                                                                                        public bool IsProperSupersetOf(IEnumerable<T> other) {
                                                                                                                                                            if (other == null) {
                                                                                                                                                                throw new ArgumentNullException("other");
                                                                                                                                                            }
                                                                                                                                                            Contract.EndContractBlock();

                                                                                                                                                            // the empty set isn't a proper superset of any set.
                                                                                                                                                            if (m_count == 0) {
                                                                                                                                                                return false;
                                                                                                                                                            }

                                                                                                                                                            ICollection<T> otherAsCollection = other as ICollection<T>;
                                                                                                                                                            if (otherAsCollection != null) {
                                                                                                                                                                // if other is the empty set then this is a superset
                                                                                                                                                                if (otherAsCollection.Count == 0) {
                                                                                                                                                                    // note that this has at least one element, based on above check
                                                                                                                                                                    return true;
                                                                                                                                                                }
                                                                                                                                                                RetrievableEntryHashSet<T> otherAsSet = other as RetrievableEntryHashSet<T>;
                                                                                                                                                                // faster if other is a hashset with the same equality comparer
                                                                                                                                                                if (otherAsSet != null && AreEqualityComparersEqual(this, otherAsSet)) {
                                                                                                                                                                    if (otherAsSet.Count >= m_count) {
                                                                                                                                                                        return false;
                                                                                                                                                                    }
                                                                                                                                                                    // now perform element check
                                                                                                                                                                    return ContainsAllElements(otherAsSet);
                                                                                                                                                                }
                                                                                                                                                            }
                                                                                                                                                            // couldn't fall out in the above cases; do it the long way
                                                                                                                                                            ElementCount result = CheckUniqueAndUnfoundElements(other, true);
                                                                                                                                                            return (result.uniqueCount < m_count && result.unfoundCount == 0);

                                                                                                                                                        }

                                                                                                                                                        /// <summary>
                                                                                                                                                        /// Checks if this set overlaps other (i.e. they share at least one item)
                                                                                                                                                        /// </summary>
                                                                                                                                                        /// <param name="other"></param>
                                                                                                                                                        /// <returns>true if these have at least one common element; false if disjoint</returns>
                                                                                                                                                        public bool Overlaps(IEnumerable<T> other) {
                                                                                                                                                            if (other == null) {
                                                                                                                                                                throw new ArgumentNullException("other");
                                                                                                                                                            }
                                                                                                                                                            Contract.EndContractBlock();

                                                                                                                                                            if (m_count == 0) {
                                                                                                                                                                return false;
                                                                                                                                                            }

                                                                                                                                                            foreach (T element in other) {
                                                                                                                                                                if (Contains(element)) {
                                                                                                                                                                    return true;
                                                                                                                                                                }
                                                                                                                                                            }
                                                                                                                                                            return false;
                                                                                                                                                        }

                                                                                                                                                        /// <summary>
                                                                                                                                                        /// Checks if this and other contain the same elements. This is set equality: 
                                                                                                                                                        /// duplicates and order are ignored
                                                                                                                                                        /// </summary>
                                                                                                                                                        /// <param name="other"></param>
                                                                                                                                                        /// <returns></returns>
                                                                                                                                                        public bool SetEquals(IEnumerable<T> other) {
                                                                                                                                                            if (other == null) {
                                                                                                                                                                throw new ArgumentNullException("other");
                                                                                                                                                            }
                                                                                                                                                            Contract.EndContractBlock();

                                                                                                                                                            RetrievableEntryHashSet<T> otherAsSet = other as RetrievableEntryHashSet<T>;
                                                                                                                                                            // faster if other is a hashset and we're using same equality comparer
                                                                                                                                                            if (otherAsSet != null && AreEqualityComparersEqual(this, otherAsSet)) {
                                                                                                                                                                // attempt to return early: since both contain unique elements, if they have 
                                                                                                                                                                // different counts, then they can't be equal
                                                                                                                                                                if (m_count != otherAsSet.Count) {
                                                                                                                                                                    return false;
                                                                                                                                                                }

                                                                                                                                                                // already confirmed that the sets have the same number of distinct elements, so if
                                                                                                                                                                // one is a superset of the other then they must be equal
                                                                                                                                                                return ContainsAllElements(otherAsSet);
                                                                                                                                                            }
                                                                                                                                                            else {
                                                                                                                                                                ICollection<T> otherAsCollection = other as ICollection<T>;
                                                                                                                                                                if (otherAsCollection != null) {
                                                                                                                                                                    // if this count is 0 but other contains at least one element, they can't be equal
                                                                                                                                                                    if (m_count == 0 && otherAsCollection.Count > 0) {
                                                                                                                                                                        return false;
                                                                                                                                                                    }
                                                                                                                                                                }
                                                                                                                                                                ElementCount result = CheckUniqueAndUnfoundElements(other, true);
                                                                                                                                                                return (result.uniqueCount == m_count && result.unfoundCount == 0);
                                                                                                                                                            }
                                                                                                                                                        }
#endif

        // Copy all elements into array starting at zero based index specified
        [SuppressMessage("Microsoft.Usage", "CA2208:InstantiateArgumentExceptionsCorrectly", Justification = "Decently informative for an exception that will probably never actually see the light of day")]
        void ICollection<KeyValuePair<string, T>>.CopyTo(KeyValuePair<string, T>[] array, int index)
        {
            if (index < 0 || Count > array.Length - index)
                throw new ArgumentException("index");

            int i = index;
            foreach (var entry in this)
            {
                array[i] = new KeyValuePair<string, T>(entry.Key, entry);
                i++;
            }
        }

        public void CopyTo(T[] array) { CopyTo(array, 0, _count); }

        [SuppressMessage("Microsoft.Usage", "CA2208:InstantiateArgumentExceptionsCorrectly", Justification = "Decently informative for an exception that will probably never actually see the light of day")]
        public void CopyTo(T[] array, int arrayIndex, int count)
        {
            if (array == null)
            {
                throw new ArgumentNullException("array");
            }
            Contract.EndContractBlock();

            // check array index valid index into array
            if (arrayIndex < 0)
            {
                throw new ArgumentOutOfRangeException("arrayIndex");
            }

            // also throw if count less than 0
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException("count");
            }

            // will array, starting at arrayIndex, be able to hold elements? Note: not
            // checking arrayIndex >= array.Length (consistency with list of allowing
            // count of 0; subsequent check takes care of the rest)
            if (arrayIndex > array.Length || count > array.Length - arrayIndex)
            {
                throw new ArgumentException("arrayIndex");
            }

            int numCopied = 0;
            for (int i = 0; i < _lastIndex && numCopied < count; i++)
            {
                if (_slots[i].hashCode >= 0)
                {
                    array[arrayIndex + numCopied] = _slots[i].value;
                    numCopied++;
                }
            }
        }

#if NEVER
                                                                                                                                                    /// <summary>
                                                                                                                                                    /// Remove elements that match specified predicate. Returns the number of elements removed
                                                                                                                                                    /// </summary>
                                                                                                                                                    /// <param name="match"></param>
                                                                                                                                                    /// <returns></returns>
                                                                                                                                                    public int RemoveWhere(Predicate<T> match) {
                                                                                                                                                        if (match == null) {
                                                                                                                                                            throw new ArgumentNullException("match");
                                                                                                                                                        }
                                                                                                                                                        Contract.EndContractBlock();

                                                                                                                                                        int numRemoved = 0;
                                                                                                                                                        for (int i = 0; i < m_lastIndex; i++) {
                                                                                                                                                            if (m_slots[i].hashCode >= 0) {
                                                                                                                                                                // cache value in case delegate removes it
                                                                                                                                                                T value = m_slots[i].value;
                                                                                                                                                                if (match(value)) {
                                                                                                                                                                    // check again that remove actually removed it
                                                                                                                                                                    if (Remove(value)) {
                                                                                                                                                                        numRemoved++;
                                                                                                                                                                    }
                                                                                                                                                                }
                                                                                                                                                            }
                                                                                                                                                        }
                                                                                                                                                        return numRemoved;
                                                                                                                                                    }
#endif
        /// <summary>
        /// Gets the IEqualityComparer that is used to determine equality of keys for 
        /// the HashSet.
        /// </summary>
        public IEqualityComparer<IKeyed> Comparer
        {
            get
            {
                return _comparer;
            }
        }

        /// <summary>
        /// Sets the capacity of this list to the size of the list (rounded up to nearest prime),
        /// unless count is 0, in which case we release references.
        /// 
        /// This method can be used to minimize a list's memory overhead once it is known that no
        /// new elements will be added to the list. To completely clear a list and release all 
        /// memory referenced by the list, execute the following statements:
        /// 
        /// list.Clear();
        /// list.TrimExcess(); 
        /// </summary>
        public void TrimExcess()
        {
            Debug.Assert(_count >= 0, "m_count is negative");

            if (_count == 0)
            {
                // if count is zero, clear references
                _buckets = null;
                _slots = null;
                _version++;
            }
            else
            {
                Debug.Assert(_buckets != null, "m_buckets was null but m_count > 0");

                // similar to IncreaseCapacity but moves down elements in case add/remove/etc
                // caused fragmentation
                int newSize = HashHelpers.GetPrime(_count);
                Slot[] newSlots = new Slot[newSize];
                int[] newBuckets = new int[newSize];

                // move down slots and rehash at the same time. newIndex keeps track of current 
                // position in newSlots array
                int newIndex = 0;
                for (int i = 0; i < _lastIndex; i++)
                {
                    if (_slots[i].hashCode >= 0)
                    {
                        newSlots[newIndex] = _slots[i];

                        // rehash
                        int bucket = newSlots[newIndex].hashCode % newSize;
                        newSlots[newIndex].next = newBuckets[bucket] - 1;
                        newBuckets[bucket] = newIndex + 1;

                        newIndex++;
                    }
                }

                Debug.Assert(newSlots.Length <= _slots.Length, "capacity increased after TrimExcess");

                _lastIndex = newIndex;
                _slots = newSlots;
                _buckets = newBuckets;
                _freeList = -1;
            }
        }

#if NEVER
#if !SILVERLIGHT || FEATURE_NETCORE
                                                                                                                                                    /// <summary>
                                                                                                                                                    /// Used for deep equality of HashSet testing
                                                                                                                                                    /// </summary>
                                                                                                                                                    /// <returns></returns>
                                                                                                                                                    public static IEqualityComparer<RetrievableEntryHashSet<T>> CreateSetComparer() {
                                                                                                                                                        return new HashSetEqualityComparer<T>();
                                                                                                                                                    }
#endif
#endif

        #endregion

        #region Helper methods

        /// <summary>
        /// Initializes buckets and slots arrays. Uses suggested capacity by finding next prime
        /// greater than or equal to capacity.
        /// </summary>
        /// <param name="capacity"></param>
        private void Initialize(int capacity)
        {
            Debug.Assert(_buckets == null, "Initialize was called but m_buckets was non-null");

            int size = HashHelpers.GetPrime(capacity);

            _buckets = new int[size];
            _slots = new Slot[size];
        }

        /// <summary>
        /// Expand to new capacity. New capacity is next prime greater than or equal to suggested 
        /// size. This is called when the underlying array is filled. This performs no 
        /// defragmentation, allowing faster execution; note that this is reasonable since 
        /// AddEvenIfPresent attempts to insert new elements in re-opened spots.
        /// </summary>
        /// <param name="sizeSuggestion"></param>
        private void IncreaseCapacity()
        {
            Debug.Assert(_buckets != null, "IncreaseCapacity called on a set with no elements");

            int newSize = HashHelpers.ExpandPrime(_count);
            if (newSize <= _count)
            {
                throw new ArgumentException("newSize");
            }

            // Able to increase capacity; copy elements to larger array and rehash
            Slot[] newSlots = new Slot[newSize];
            if (_slots != null)
            {
                Array.Copy(_slots, 0, newSlots, 0, _lastIndex);
            }

            int[] newBuckets = new int[newSize];
            for (int i = 0; i < _lastIndex; i++)
            {
                int bucket = newSlots[i].hashCode % newSize;
                newSlots[i].next = newBuckets[bucket] - 1;
                newBuckets[bucket] = i + 1;
            }
            _slots = newSlots;
            _buckets = newBuckets;
        }

        /// <summary>
        /// Adds value to HashSet if not contained already
        /// Returns true if added and false if already present
        /// ** MSBUILD: Modified so that it DOES add even if present. It will return false in that case, though.**
        /// </summary>
        /// <param name="value">value to find</param>
        /// <returns></returns>
        private bool AddEvenIfPresent(T value)
        {
            if (_readOnly)
            {
                ErrorUtilities.ThrowInvalidOperation("OM_NotSupportedReadOnlyCollection");
            }

            if (_buckets == null)
            {
                Initialize(0);
            }

            int hashCode = InternalGetHashCode(value);
            int bucket = hashCode % _buckets.Length;
            for (int i = _buckets[hashCode % _buckets.Length] - 1; i >= 0; i = _slots[i].next)
            {
                if (_slots[i].hashCode == hashCode && _comparer.Equals(_slots[i].value, value))
                {
                    // NOTE: this must add EVEN IF it is already present,
                    // as it may be a different object with the same name,
                    // and we want "last wins" semantics
                    _slots[i].hashCode = hashCode;
                    _slots[i].value = value;
                    return false;
                }
            }
            int index;
            if (_freeList >= 0)
            {
                index = _freeList;
                _freeList = _slots[index].next;
            }
            else
            {
                if (_lastIndex == _slots.Length)
                {
                    IncreaseCapacity();
                    // this will change during resize
                    bucket = hashCode % _buckets.Length;
                }
                index = _lastIndex;
                _lastIndex++;
            }
            _slots[index].hashCode = hashCode;
            _slots[index].value = value;
            _slots[index].next = _buckets[bucket] - 1;
            _buckets[bucket] = index + 1;
            _count++;
            _version++;
            return true;
        }

        /// <summary>
        /// Equality comparer against another of this type.
        /// Compares entries by reference - not merely by using the comparer on the key
        /// </summary>
        internal bool EntriesAreReferenceEquals(RetrievableEntryHashSet<T> other)
        {
            if (Object.ReferenceEquals(this, other))
            {
                return true;
            }

            if (this.Count != other.Count)
            {
                return false;
            }

            T ours;
            foreach (T element in other)
            {
                if (!TryGetValue(element.Key, out ours) || !Object.ReferenceEquals(element, ours))
                {
                    return false;
                }
            }

            return true;
        }

#if NEVER
                                                                                                                                                                        /// <summary>
                                                                                                                                                                        /// Checks if this contains of other's elements. Iterates over other's elements and 
                                                                                                                                                                        /// returns false as soon as it finds an element in other that's not in this.
                                                                                                                                                                        /// Used by SupersetOf, ProperSupersetOf, and SetEquals.
                                                                                                                                                                        /// </summary>
                                                                                                                                                                        /// <param name="other"></param>
                                                                                                                                                                        /// <returns></returns>
                                                                                                                                                                        private bool ContainsAllElements(IEnumerable<T> other) {
                                                                                                                                                                            foreach (T element in other) {
                                                                                                                                                                                if (!Contains(element)) {
                                                                                                                                                                                    return false;
                                                                                                                                                                                }
                                                                                                                                                                            }
                                                                                                                                                                            return true;
                                                                                                                                                                        }

                                                                                                                                                                        /// <summary>
                                                                                                                                                                        /// Implementation Notes:
                                                                                                                                                                        /// If other is a hashset and is using same equality comparer, then checking subset is 
                                                                                                                                                                        /// faster. Simply check that each element in this is in other.
                                                                                                                                                                        /// 
                                                                                                                                                                        /// Note: if other doesn't use same equality comparer, then Contains check is invalid,
                                                                                                                                                                        /// which is why callers must take are of this.
                                                                                                                                                                        /// 
                                                                                                                                                                        /// If callers are concerned about whether this is a proper subset, they take care of that.
                                                                                                                                                                        ///
                                                                                                                                                                        /// </summary>
                                                                                                                                                                        /// <param name="other"></param>
                                                                                                                                                                        /// <returns></returns>
                                                                                                                                                                        private bool IsSubsetOfHashSetWithSameEC(RetrievableEntryHashSet<T> other) {

                                                                                                                                                                            foreach (T item in this) {
                                                                                                                                                                                if (!other.Contains(item)) {
                                                                                                                                                                                    return false;
                                                                                                                                                                                }
                                                                                                                                                                            }
                                                                                                                                                                            return true;
                                                                                                                                                                        }

                                                                                                                                                                        /// <summary>
                                                                                                                                                                        /// If other is a hashset that uses same equality comparer, intersect is much faster 
                                                                                                                                                                        /// because we can use other's Contains
                                                                                                                                                                        /// </summary>
                                                                                                                                                                        /// <param name="other"></param>
                                                                                                                                                                        private void IntersectWithHashSetWithSameEC(RetrievableEntryHashSet<T> other) {
                                                                                                                                                                            for (int i = 0; i < m_lastIndex; i++) {
                                                                                                                                                                                if (m_slots[i].hashCode >= 0) {
                                                                                                                                                                                    T item = m_slots[i].value;
                                                                                                                                                                                    if (!other.Contains(item)) {
                                                                                                                                                                                        Remove(item);
                                                                                                                                                                                    }
                                                                                                                                                                                }
                                                                                                                                                                            }
                                                                                                                                                                        }

                                                                                                                                                                        /// <summary>
                                                                                                                                                                        /// Iterate over other. If contained in this, mark an element in bit array corresponding to
                                                                                                                                                                        /// its position in m_slots. If anything is unmarked (in bit array), remove it.
                                                                                                                                                                        /// 
                                                                                                                                                                        /// This attempts to allocate on the stack, if below StackAllocThreshold.
                                                                                                                                                                        /// </summary>
                                                                                                                                                                        /// <param name="other"></param>
                                                                                                                                                                        [System.Security.SecuritySafeCritical]
                                                                                                                                                                        private unsafe void IntersectWithEnumerable(IEnumerable<T> other) {
                                                                                                                                                                            Debug.Assert(m_buckets != null, "m_buckets shouldn't be null; callers should check first");

                                                                                                                                                                            // keep track of current last index; don't want to move past the end of our bit array
                                                                                                                                                                            // (could happen if another thread is modifying the collection)
                                                                                                                                                                            int originalLastIndex = m_lastIndex;
                                                                                                                                                                            int intArrayLength = BitHelper.ToIntArrayLength(originalLastIndex);

                                                                                                                                                                            BitHelper bitHelper;
                                                                                                                                                                            if (intArrayLength <= StackAllocThreshold) {
                                                                                                                                                                                int* bitArrayPtr = stackalloc int[intArrayLength];
                                                                                                                                                                                bitHelper = new BitHelper(bitArrayPtr, intArrayLength);
                                                                                                                                                                            }
                                                                                                                                                                            else {
                                                                                                                                                                                int[] bitArray = new int[intArrayLength];
                                                                                                                                                                                bitHelper = new BitHelper(bitArray, intArrayLength);
                                                                                                                                                                            }

                                                                                                                                                                            // mark if contains: find index of in slots array and mark corresponding element in bit array
                                                                                                                                                                            foreach (T item in other) {
                                                                                                                                                                                int index = InternalIndexOf(item);
                                                                                                                                                                                if (index >= 0) {
                                                                                                                                                                                    bitHelper.MarkBit(index);
                                                                                                                                                                                }
                                                                                                                                                                            }

                                                                                                                                                                            // if anything unmarked, remove it. Perf can be optimized here if BitHelper had a 
                                                                                                                                                                            // FindFirstUnmarked method.
                                                                                                                                                                            for (int i = 0; i < originalLastIndex; i++) {
                                                                                                                                                                                if (m_slots[i].hashCode >= 0 && !bitHelper.IsMarked(i)) {
                                                                                                                                                                                    Remove(m_slots[i].value);
                                                                                                                                                                                }
                                                                                                                                                                            }
                                                                                                                                                                        }

                                                                                                                                                                    /// <summary>
                                                                                                                                                                    /// Used internally by set operations which have to rely on bit array marking. This is like
                                                                                                                                                                    /// Contains but returns index in slots array. 
                                                                                                                                                                    /// </summary>
                                                                                                                                                                    /// <param name="item"></param>
                                                                                                                                                                    /// <returns></returns>
                                                                                                                                                                    private int InternalIndexOf(T item) {
                                                                                                                                                                        Debug.Assert(m_buckets != null, "m_buckets was null; callers should check first");

                                                                                                                                                                        int hashCode = InternalGetHashCode(item);
                                                                                                                                                                        for (int i = m_buckets[hashCode % m_buckets.Length] - 1; i >= 0; i = m_slots[i].next) {
                                                                                                                                                                            if ((m_slots[i].hashCode) == hashCode && m_comparer.Equals(m_slots[i].value, item)) {
                                                                                                                                                                                return i;
                                                                                                                                                                            }
                                                                                                                                                                        }
                                                                                                                                                                        // wasn't found
                                                                                                                                                                        return -1;
                                                                                                                                                                    }

                                                                                                                                                                /// <summary>
                                                                                                                                                                /// if other is a set, we can assume it doesn't have duplicate elements, so use this
                                                                                                                                                                /// technique: if can't remove, then it wasn't present in this set, so add.
                                                                                                                                                                /// 
                                                                                                                                                                /// As with other methods, callers take care of ensuring that other is a hashset using the
                                                                                                                                                                /// same equality comparer.
                                                                                                                                                                /// </summary>
                                                                                                                                                                /// <param name="other"></param>
                                                                                                                                                                private void SymmetricExceptWithUniqueHashSet(RetrievableEntryHashSet<T> other) {
                                                                                                                                                                    foreach (T item in other) {
                                                                                                                                                                        if (!Remove(item)) {
                                                                                                                                                                            AddEvenIfPresent(item);
                                                                                                                                                                        }
                                                                                                                                                                    }
                                                                                                                                                                }

                                                                                                                                                                /// <summary>
                                                                                                                                                                /// Implementation notes:
                                                                                                                                                                /// 
                                                                                                                                                                /// Used for symmetric except when other isn't a HashSet. This is more tedious because 
                                                                                                                                                                /// other may contain duplicates. HashSet technique could fail in these situations:
                                                                                                                                                                /// 1. Other has a duplicate that's not in this: HashSet technique would add then 
                                                                                                                                                                /// remove it.
                                                                                                                                                                /// 2. Other has a duplicate that's in this: HashSet technique would remove then add it
                                                                                                                                                                /// back.
                                                                                                                                                                /// In general, its presence would be toggled each time it appears in other. 
                                                                                                                                                                /// 
                                                                                                                                                                /// This technique uses bit marking to indicate whether to add/remove the item. If already
                                                                                                                                                                /// present in collection, it will get marked for deletion. If added from other, it will
                                                                                                                                                                /// get marked as something not to remove.
                                                                                                                                                                ///
                                                                                                                                                                /// </summary>
                                                                                                                                                                /// <param name="other"></param>
                                                                                                                                                                [System.Security.SecuritySafeCritical]
                                                                                                                                                                private unsafe void SymmetricExceptWithEnumerable(IEnumerable<T> other) {
                                                                                                                                                                    int originalLastIndex = m_lastIndex;
                                                                                                                                                                    int intArrayLength = BitHelper.ToIntArrayLength(originalLastIndex);

                                                                                                                                                                    BitHelper itemsToRemove;
                                                                                                                                                                    BitHelper itemsAddedFromOther;
                                                                                                                                                                    if (intArrayLength <= StackAllocThreshold / 2) {
                                                                                                                                                                        int* itemsToRemovePtr = stackalloc int[intArrayLength];
                                                                                                                                                                        itemsToRemove = new BitHelper(itemsToRemovePtr, intArrayLength);

                                                                                                                                                                        int* itemsAddedFromOtherPtr = stackalloc int[intArrayLength];
                                                                                                                                                                        itemsAddedFromOther = new BitHelper(itemsAddedFromOtherPtr, intArrayLength);
                                                                                                                                                                    }
                                                                                                                                                                    else {
                                                                                                                                                                        int[] itemsToRemoveArray = new int[intArrayLength];
                                                                                                                                                                        itemsToRemove = new BitHelper(itemsToRemoveArray, intArrayLength);

                                                                                                                                                                        int[] itemsAddedFromOtherArray = new int[intArrayLength];
                                                                                                                                                                        itemsAddedFromOther = new BitHelper(itemsAddedFromOtherArray, intArrayLength);
                                                                                                                                                                    }

                                                                                                                                                                    foreach (T item in other) {
                                                                                                                                                                        int location = 0;
                                                                                                                                                                        bool added = AddOrGetLocation(item, out location);
                                                                                                                                                                        if (added) {
                                                                                                                                                                            // wasn't already present in collection; flag it as something not to remove
                                                                                                                                                                            // *NOTE* if location is out of range, we should ignore. BitHelper will
                                                                                                                                                                            // detect that it's out of bounds and not try to mark it. But it's 
                                                                                                                                                                            // expected that location could be out of bounds because adding the item
                                                                                                                                                                            // will increase m_lastIndex as soon as all the free spots are filled.
                                                                                                                                                                            itemsAddedFromOther.MarkBit(location);
                                                                                                                                                                        }
                                                                                                                                                                        else {
                                                                                                                                                                            // already there...if not added from other, mark for remove. 
                                                                                                                                                                            // *NOTE* Even though BitHelper will check that location is in range, we want 
                                                                                                                                                                            // to check here. There's no point in checking items beyond originalLastIndex
                                                                                                                                                                            // because they could not have been in the original collection
                                                                                                                                                                            if (location < originalLastIndex && !itemsAddedFromOther.IsMarked(location)) {
                                                                                                                                                                                itemsToRemove.MarkBit(location);
                                                                                                                                                                            }
                                                                                                                                                                        }
                                                                                                                                                                    }

                                                                                                                                                                    // if anything marked, remove it
                                                                                                                                                                    for (int i = 0; i < originalLastIndex; i++) {
                                                                                                                                                                        if (itemsToRemove.IsMarked(i)) {
                                                                                                                                                                            Remove(m_slots[i].value);
                                                                                                                                                                        }
                                                                                                                                                                    }
                                                                                                                                                                }

                                                                                                                                                                /// <summary>
                                                                                                                                                                /// Add if not already in hashset. Returns an out param indicating index where added. This 
                                                                                                                                                                /// is used by SymmetricExcept because it needs to know the following things:
                                                                                                                                                                /// - whether the item was already present in the collection or added from other
                                                                                                                                                                /// - where it's located (if already present, it will get marked for removal, otherwise
                                                                                                                                                                /// marked for keeping)
                                                                                                                                                                /// </summary>
                                                                                                                                                                /// <param name="value"></param>
                                                                                                                                                                /// <param name="location"></param>
                                                                                                                                                                /// <returns></returns>
                                                                                                                                                                private bool AddOrGetLocation(T value, out int location) {
                                                                                                                                                                    Debug.Assert(m_buckets != null, "m_buckets is null, callers should have checked");

                                                                                                                                                                    int hashCode = InternalGetHashCode(value);
                                                                                                                                                                    int bucket = hashCode % m_buckets.Length;
                                                                                                                                                                    for (int i = m_buckets[hashCode % m_buckets.Length] - 1; i >= 0; i = m_slots[i].next) {
                                                                                                                                                                        if (m_slots[i].hashCode == hashCode && m_comparer.Equals(m_slots[i].value, value)) {
                                                                                                                                                                            location = i;
                                                                                                                                                                            return false; //already present
                                                                                                                                                                        }
                                                                                                                                                                    }
                                                                                                                                                                    int index;
                                                                                                                                                                    if (m_freeList >= 0) {
                                                                                                                                                                        index = m_freeList;
                                                                                                                                                                        m_freeList = m_slots[index].next;
                                                                                                                                                                    }
                                                                                                                                                                    else {
                                                                                                                                                                        if (m_lastIndex == m_slots.Length) {
                                                                                                                                                                            IncreaseCapacity();
                                                                                                                                                                            // this will change during resize
                                                                                                                                                                            bucket = hashCode % m_buckets.Length;
                                                                                                                                                                        }
                                                                                                                                                                        index = m_lastIndex;
                                                                                                                                                                        m_lastIndex++;
                                                                                                                                                                    }
                                                                                                                                                                    m_slots[index].hashCode = hashCode;
                                                                                                                                                                    m_slots[index].value = value;
                                                                                                                                                                    m_slots[index].next = m_buckets[bucket] - 1;
                                                                                                                                                                    m_buckets[bucket] = index + 1;
                                                                                                                                                                    m_count++;
                                                                                                                                                                    m_version++;
                                                                                                                                                                    location = index;
                                                                                                                                                                    return true;
                                                                                                                                                                }

                                                                                                                                                                /// <summary>
                                                                                                                                                                /// Determines counts that can be used to determine equality, subset, and superset. This
                                                                                                                                                                /// is only used when other is an IEnumerable and not a HashSet. If other is a HashSet
                                                                                                                                                                /// these properties can be checked faster without use of marking because we can assume 
                                                                                                                                                                /// other has no duplicates.
                                                                                                                                                                /// 
                                                                                                                                                                /// The following count checks are performed by callers:
                                                                                                                                                                /// 1. Equals: checks if unfoundCount = 0 and uniqueFoundCount = m_count; i.e. everything 
                                                                                                                                                                /// in other is in this and everything in this is in other
                                                                                                                                                                /// 2. Subset: checks if unfoundCount >= 0 and uniqueFoundCount = m_count; i.e. other may
                                                                                                                                                                /// have elements not in this and everything in this is in other
                                                                                                                                                                /// 3. Proper subset: checks if unfoundCount > 0 and uniqueFoundCount = m_count; i.e
                                                                                                                                                                /// other must have at least one element not in this and everything in this is in other
                                                                                                                                                                /// 4. Proper superset: checks if unfound count = 0 and uniqueFoundCount strictly less
                                                                                                                                                                /// than m_count; i.e. everything in other was in this and this had at least one element
                                                                                                                                                                /// not contained in other.
                                                                                                                                                                /// 
                                                                                                                                                                /// An earlier implementation used delegates to perform these checks rather than returning
                                                                                                                                                                /// an ElementCount struct; however this was changed due to the perf overhead of delegates.
                                                                                                                                                                /// </summary>
                                                                                                                                                                /// <param name="other"></param>
                                                                                                                                                                /// <param name="returnIfUnfound">Allows us to finish faster for equals and proper superset
                                                                                                                                                                /// because unfoundCount must be 0.</param>
                                                                                                                                                                /// <returns></returns>
                                                                                                                                                                [System.Security.SecuritySafeCritical]
                                                                                                                                                                private unsafe ElementCount CheckUniqueAndUnfoundElements(IEnumerable<T> other, bool returnIfUnfound) {
                                                                                                                                                                    ElementCount result;

                                                                                                                                                                    // need special case in case this has no elements. 
                                                                                                                                                                    if (m_count == 0) {
                                                                                                                                                                        int numElementsInOther = 0;
                                                                                                                                                                        foreach (T item in other) {
                                                                                                                                                                            numElementsInOther++;
                                                                                                                                                                            // break right away, all we want to know is whether other has 0 or 1 elements
                                                                                                                                                                            break;
                                                                                                                                                                        }
                                                                                                                                                                        result.uniqueCount = 0;
                                                                                                                                                                        result.unfoundCount = numElementsInOther;
                                                                                                                                                                        return result;
                                                                                                                                                                    }


                                                                                                                                                                    Debug.Assert((m_buckets != null) && (m_count > 0), "m_buckets was null but count greater than 0");

                                                                                                                                                                    int originalLastIndex = m_lastIndex;
                                                                                                                                                                    int intArrayLength = BitHelper.ToIntArrayLength(originalLastIndex);

                                                                                                                                                                    BitHelper bitHelper;
                                                                                                                                                                    if (intArrayLength <= StackAllocThreshold) {
                                                                                                                                                                        int* bitArrayPtr = stackalloc int[intArrayLength];
                                                                                                                                                                        bitHelper = new BitHelper(bitArrayPtr, intArrayLength);
                                                                                                                                                                    }
                                                                                                                                                                    else {
                                                                                                                                                                        int[] bitArray = new int[intArrayLength];
                                                                                                                                                                        bitHelper = new BitHelper(bitArray, intArrayLength);
                                                                                                                                                                    }

                                                                                                                                                                    // count of items in other not found in this
                                                                                                                                                                    int unfoundCount = 0;
                                                                                                                                                                    // count of unique items in other found in this
                                                                                                                                                                    int uniqueFoundCount = 0;

                                                                                                                                                                    foreach (T item in other) {
                                                                                                                                                                        int index = InternalIndexOf(item);
                                                                                                                                                                        if (index >= 0) {
                                                                                                                                                                            if (!bitHelper.IsMarked(index)) {
                                                                                                                                                                                // item hasn't been seen yet
                                                                                                                                                                                bitHelper.MarkBit(index);
                                                                                                                                                                                uniqueFoundCount++;
                                                                                                                                                                            }
                                                                                                                                                                        }
                                                                                                                                                                        else {
                                                                                                                                                                            unfoundCount++;
                                                                                                                                                                            if (returnIfUnfound) {
                                                                                                                                                                                break;
                                                                                                                                                                            }
                                                                                                                                                                        }
                                                                                                                                                                    }

                                                                                                                                                                    result.uniqueCount = uniqueFoundCount;
                                                                                                                                                                    result.unfoundCount = unfoundCount;
                                                                                                                                                                    return result;
                                                                                                                                                                }
#endif
        /// <summary>
        /// Copies this to an array. Used for DebugView
        /// </summary>
        /// <returns></returns>
        internal T[] ToArray()
        {
            T[] newArray = new T[Count];
            CopyTo(newArray);
            return newArray;
        }

#if NEVER
                                                                                                                                                            /// <summary>
                                                                                                                                                            /// Internal method used for HashSetEqualityComparer. Compares set1 and set2 according 
                                                                                                                                                            /// to specified comparer.
                                                                                                                                                            /// 
                                                                                                                                                            /// Because items are hashed according to a specific equality comparer, we have to resort
                                                                                                                                                            /// to n^2 search if they're using different equality comparers.
                                                                                                                                                            /// </summary>
                                                                                                                                                            /// <param name="set1"></param>
                                                                                                                                                            /// <param name="set2"></param>
                                                                                                                                                            /// <param name="comparer"></param>
                                                                                                                                                            /// <returns></returns>
                                                                                                                                                            internal static bool HashSetEquals(RetrievableEntryHashSet<T> set1, RetrievableEntryHashSet<T> set2, IEqualityComparer<T> comparer) {
                                                                                                                                                                // handle null cases first
                                                                                                                                                                if (set1 == null) {
                                                                                                                                                                    return (set2 == null);
                                                                                                                                                                }
                                                                                                                                                                else if (set2 == null) {
                                                                                                                                                                    // set1 != null
                                                                                                                                                                    return false;
                                                                                                                                                                }

                                                                                                                                                                // all comparers are the same; this is faster
                                                                                                                                                                if (AreEqualityComparersEqual(set1, set2)) {
                                                                                                                                                                    if (set1.Count != set2.Count) {
                                                                                                                                                                        return false;
                                                                                                                                                                    }
                                                                                                                                                                    // suffices to check subset
                                                                                                                                                                    foreach (T item in set2) {
                                                                                                                                                                        if (!set1.Contains(item)) {
                                                                                                                                                                            return false;
                                                                                                                                                                        }
                                                                                                                                                                    }
                                                                                                                                                                    return true;
                                                                                                                                                                }
                                                                                                                                                                else {  // n^2 search because items are hashed according to their respective ECs
                                                                                                                                                                    foreach (T set2Item in set2) {
                                                                                                                                                                        bool found = false;
                                                                                                                                                                        foreach (T set1Item in set1) {
                                                                                                                                                                            if (comparer.Equals(set2Item, set1Item)) {
                                                                                                                                                                                found = true;
                                                                                                                                                                                break;
                                                                                                                                                                            }
                                                                                                                                                                        }
                                                                                                                                                                        if (!found) {
                                                                                                                                                                            return false;
                                                                                                                                                                        }
                                                                                                                                                                    }
                                                                                                                                                                    return true;
                                                                                                                                                                }
                                                                                                                                                            }

                                                                                                                                                            /// <summary>
                                                                                                                                                            /// Checks if equality comparers are equal. This is used for algorithms that can
                                                                                                                                                            /// speed up if it knows the other item has unique elements. I.e. if they're using 
                                                                                                                                                            /// different equality comparers, then uniqueness assumption between sets break.
                                                                                                                                                            /// </summary>
                                                                                                                                                            /// <param name="set1"></param>
                                                                                                                                                            /// <param name="set2"></param>
                                                                                                                                                            /// <returns></returns>
                                                                                                                                                            private static bool AreEqualityComparersEqual(RetrievableEntryHashSet<T> set1, RetrievableEntryHashSet<T> set2) {
                                                                                                                                                                return set1.Comparer.Equals(set2.Comparer);
        }
#endif
        /// <summary>
        /// Workaround Comparers that throw ArgumentNullException for GetHashCode(null).
        /// </summary>
        /// <param name="item"></param>
        /// <returns>hash code</returns>
        private int InternalGetHashCode(IKeyed item)
        {
            if (item == null)
            {
                return 0;
            }
            return _comparer.GetHashCode(item) & Lower31BitMask;
        }

        #endregion

        // used for set checking operations (using enumerables) that rely on counting
        internal struct ElementCount
        {
            internal int uniqueCount;
            internal int unfoundCount;
        }

        internal struct Slot
        {
            internal int hashCode;      // Lower 31 bits of hash code, -1 if unused
            internal T value;
            internal int next;          // Index of next entry, -1 if last
        }

#if !SILVERLIGHT
        [Serializable()]
#if !MONO
        [System.Security.Permissions.HostProtection(MayLeakOnAbort = true)]
#endif
#endif
        public struct Enumerator : IEnumerator<T>, System.Collections.IEnumerator
        {
            private RetrievableEntryHashSet<T> _set;
            private int _index;
            private int _version;
            private T _current;

            internal Enumerator(RetrievableEntryHashSet<T> set)
            {
                _set = set;
                _index = 0;
                _version = set._version;
                _current = default(T);
            }

            public void Dispose()
            {
            }

            public bool MoveNext()
            {
                if (_version != _set._version)
                {
                    throw new InvalidOperationException();
                }

                while (_index < _set._lastIndex)
                {
                    if (_set._slots[_index].hashCode >= 0)
                    {
                        _current = _set._slots[_index].value;
                        _index++;
                        return true;
                    }
                    _index++;
                }
                _index = _set._lastIndex + 1;
                _current = default(T);
                return false;
            }

            public T Current
            {
                get
                {
                    return _current;
                }
            }

            Object System.Collections.IEnumerator.Current
            {
                get
                {
                    if (_index == 0 || _index == _set._lastIndex + 1)
                    {
                        throw new InvalidOperationException();
                    }
                    return Current;
                }
            }

            void System.Collections.IEnumerator.Reset()
            {
                if (_version != _set._version)
                {
                    throw new InvalidOperationException();
                }

                _index = 0;
                _current = default(T);
            }
        }

        /// <summary>
        /// Wrapper is necessary because String doesn't implement IKeyed
        /// </summary>
        private struct KeyedObject : IKeyed
        {
            private string _name;

            internal KeyedObject(string name)
            {
                _name = name;
            }

            string IKeyed.Key
            {
                get { return _name; }
            }

            public override int GetHashCode()
            {
                ErrorUtilities.ThrowInternalError("should be using comparer");
                return -1;
            }
        }
    }
}
