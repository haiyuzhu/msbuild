// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Resources;
using System.Text;
using Microsoft.Extensions.ObjectPool;

namespace Microsoft.Build.Shared
{
    /// <summary>
    /// Represents a string that can be converted to System.String with interning, i.e. by returning an existing string if it has been seen before
    /// and is still tracked in the intern table.
    /// </summary>
    /// <remarks>
    /// The string is represented with a series of character spans describing the fragments making up the string. There are two principal use cases:
    /// 
    /// 1) An internable System.String, aka a string wrapper.
    ///    Wraps an existing string, substring, character array/sub-array, char *, or any other ReadOnlySpan&lt;char&gt; so it can be interned.
    ///    The advantage over first creating the desired System.String and then interning it is that it does away with the ephemeral System.String
    ///    allocation in the case where the string already is in the intern table.
    ///
    ///    var str = new InternableString("semicolon;delimited".AsSpan(0, 8));
    ///    Console.WriteLine(str.ToString()); // prints "semicolon"
    ///
    ///    Memory characteristics of the above snippet:
    ///    - InternableString is allocated on stack only,
    ///    - The ToString() call does not allocate the string "semicolon" if it already exists in the intern table.
    ///
    /// 2) An internable StringBuilder.
    ///    Compansates for the fact that System.Text.StringBuilder cannot be represented with one ReadOnlySpan&lt;char&gt; and that it does not
    ///    provide linear time character enumeration. The usage pattern is similar to that of a StringBuilder but it does not allocate O(N) bytes
    ///    where N is the intermediate string length, but rather allocates only spans describing its constituent pieces. Note that in degenerated
    ///    cases this may be worse than O(N) so make sure it's used only where it's actually helping.
    ///
    ///    var str = new InternableString();
    ///    str.Append("semicolon");
    ///    str.Append(";");
    ///    str.Append("delimited");
    ///    Console.WriteLine(str.ToString()); // prints "semicolon;delimited"
    /// 
    ///    Memory characteristics of the above snippet:
    ///    - InternableString allocates a List of span descriptors on the GC heap, the size is O(S) where S is the number of spans,
    ///    - The Append() calls do not allocate memory,
    ///    - The ToString() call does not allocate the string "semicolon;delimited" if it already exists in the intern table.
    /// </remarks>
    internal ref struct InternableString
    {
        /// <summary>
        /// Enumerator for the top-level struct. Enumerates characters of the string.
        /// </summary>
        public ref struct Enumerator
        {
            /// <summary>
            /// The InternableString being enumerated.
            /// </summary>
            private InternableString _string;

            /// <summary>
            /// Index of the current span, -1 represents the inline span.
            /// </summary>
            private int _spanIndex;

            /// <summary>
            /// Index of the current character in the current span, -1 if MoveNext has not been called yet.
            /// </summary>
            private int _charIndex;

            internal Enumerator(ref InternableString str)
            {
                _string = str;
                _spanIndex = -1;
                _charIndex = -1;
            }

            /// <summary>
            /// Returns the current character.
            /// </summary>
            public ref readonly char Current
            {
                get
                {
                    if (_spanIndex == -1)
                    {
                        return ref _string._inlineSpan[_charIndex];
                    }
                    ReadOnlyMemory<char> span = _string._spans[_spanIndex];
                    return ref span.Span[_charIndex];
                }
            }

            /// <summary>
            /// Moves to the next character.
            /// </summary>
            /// <returns>True if there is another character, false if the enumerator reached the end.</returns>
            public bool MoveNext()
            {
                int newCharIndex = _charIndex + 1;
                if (_spanIndex == -1)
                {
                    if (newCharIndex < _string._inlineSpan.Length)
                    {
                        _charIndex = newCharIndex;
                        return true;
                    }
                    _spanIndex = 0;
                    newCharIndex = 0;
                }

                if (_string._spans != null)
                {
                    while (_spanIndex < _string._spans.Count)
                    {
                        if (newCharIndex < _string._spans[_spanIndex].Length)
                        {
                            _charIndex = newCharIndex;
                            return true;
                        }
                        _spanIndex++;
                        newCharIndex = 0;
                    }
                }
                return false;
            }
        }

        /// <summary>
        /// IPooledObjectPolicy used by <cref see="s_arrayListPool"/>.
        /// </summary>
        private class PooledObjectPolicy : IPooledObjectPolicy<List<ReadOnlyMemory<char>>>
        {
            /// <summary>
            /// No need to retain excessively long list forever.
            /// </summary>
            private const int MAX_RETAINED_LIST_CAPACITY = 1000;

            /// <summary>
            /// Creates a new list with the default capacity.
            /// </summary>
            public List<ReadOnlyMemory<char>> Create()
            {
                return new List<ReadOnlyMemory<char>>();
            }

            /// <summary>
            /// Returns a list to the pool unless it's excessively long.
            /// </summary>
            public bool Return(List<ReadOnlyMemory<char>> obj)
            {
                if (obj.Capacity <= MAX_RETAINED_LIST_CAPACITY)
                {
                    obj.Clear();
                    return true;
                }
                return false;
            }
        }

        /// <summary>
        /// A pool of lists as we don't want to be allocating a new one every time an InternableString is created / appended to. Since
        /// InternableString instances must live on the stack, there is rarely more than one used by a given thread. Thus using the number
        /// of CPUs should be a reasonable heuristic.
        /// </summary>
        private static DefaultObjectPool<List<ReadOnlyMemory<char>>> s_arrayListPool =
            new DefaultObjectPool<List<ReadOnlyMemory<char>>>(new PooledObjectPolicy(), Environment.ProcessorCount);

        /// <summary>
        /// The first span held by this struct, inline to be able to represent ReadOnlySpan&lt;char&gt;. May be empty.
        /// </summary>
        private ReadOnlySpan<char> _inlineSpan;

#if NETCOREAPP
        /// <summary>
        /// .NET Core does not keep a reference to the containing object in ReadOnlySpan&lt;T&gt;. In particular, it cannot recover
        /// the string if the span represents one. We have to hold the reference separately to be able to roundtrip
        /// String-&gt;InternableString-&gt;String without allocating a new String.
        /// </summary>
        private string _inlineSpanString;
#endif

        /// <summary>
        /// Additional spans held by this struct (logically following _inlineSpan). May be null.
        /// </summary>
        private List<ReadOnlyMemory<char>> _spans;

        /// <summary>
        /// Constructs a new InternableString wrapping the given ReadOnlySpan&lt;char&gt;. The struct is still mutable and can be
        /// used as a StringBuilder, although mutations may require an allocation.
        /// </summary>
        /// <param name="span">The span to wrap.</param>
        /// <remarks>
        /// When wrapping a span representing an entire System.String, use Internable(string) for optimum performance.
        /// </remarks>
        public InternableString(ReadOnlySpan<char> span)
        {
            _inlineSpan = span;
            _spans = null;
            Length = span.Length;
#if NETCOREAPP
            _inlineSpanString = null;
#endif
        }

        /// <summary>
        /// Constructs a new InternableString wrapping the given string. The instance is still mutable and can be used as a StringBuilder,
        /// although that may require an allocation.
        /// </summary>
        /// <param name="str">The string to wrap, must be non-null.</param>
        public InternableString(string str)
        {
            if (str == null)
            {
                throw new ArgumentNullException(nameof(str));
            }

            _inlineSpan = str.AsSpan();
            _spans = null;
            Length = str.Length;
#if NETCOREAPP
            _inlineSpanString = str;
#endif
        }

        /// <summary>
        /// Constructs a new empty InternableString with the given expected number of spans. Such an InternableString is used similarly
        /// to a StringBuilder. This constructor allocates GC memory.
        /// </summary>
        public InternableString(int capacity = 4)
        {
            _inlineSpan = default(ReadOnlySpan<char>);
            _spans = s_arrayListPool.Get();
            if (_spans.Capacity < capacity)
            {
                _spans.Capacity = capacity;
            }
            Length = 0;
#if NETCOREAPP
            _inlineSpanString = null;
#endif
        }

        /// <summary>
        /// Gets the length of the string.
        /// </summary>
        public int Length { get; private set; }

        /// <summary>
        /// A convenience static method to intern a System.String.
        /// </summary>
        /// <param name="str">The string to intern.</param>
        /// <returns>A string identical in content to <paramref name="str"/>.</returns>
        public static string Intern(string str)
        {
            return new InternableString(str).ToString();
        }

        /// <summary>
        /// Creates a new enumerator for enumerating characters in this string. Does not allocate.
        /// </summary>
        /// <returns>The enumerator.</returns>
        public Enumerator GetEnumerator()
        {
            return new Enumerator(ref this);
        }

        /// <summary>
        /// Returns the character at the given index.
        /// </summary>
        /// <param name="index">The index to return the character at.</param>
        /// <returns>The character.</returns>
        /// <remarks>
        /// Similar to StringBuilder, this indexer does not work in constant time and may take O(N) where N is the index
        /// in the worst case. Use <see cref="GetEnumerator"/> for scanning the string in a linear fashion.
        /// </remarks>
        public char this[int index]
        {
            get
            {
                ErrorUtilities.VerifyThrowArgumentOutOfRange(index >= 0 && index < Length, nameof(index));

                if (index < _inlineSpan.Length)
                {
                    return _inlineSpan[index];
                }
                index -= _inlineSpan.Length;

                foreach (ReadOnlyMemory<char> span in _spans)
                {
                    if (index < span.Length)
                    {
                        return span.Span[index];
                    }
                    index -= span.Length;
                }

                ErrorUtilities.ThrowInternalError("Inconsistent {0} state", nameof(InternableString));
                throw new InvalidOperationException();
            }
        }

        /// <summary>
        /// Returns true if the string starts with another string.
        /// </summary>
        /// <param name="other">Another string.</param>
        /// <returns>True if this string starts with <paramref name="other"/>.</returns>
        public bool StartsWithStringByOrdinalComparison(string other)
        {
            int otherLength = other.Length;
            if (otherLength > Length)
            {
                // Can't start with a string which is longer.
                return false;
            }

            if (otherLength <= _inlineSpan.Length)
            {
                return _inlineSpan.StartsWith(other.AsSpan());
            }
            if (_inlineSpan.CompareTo(other.AsSpan(0, _inlineSpan.Length), StringComparison.Ordinal) != 0)
            {
                return false;
            }

            int otherStart = _inlineSpan.Length;
            foreach (ReadOnlyMemory<char> span in _spans)
            {
                if (otherLength - otherStart <= span.Length)
                {
                    return span.Span.StartsWith(other.AsSpan(otherStart, otherLength - otherStart));
                }
                if (span.Span.CompareTo(other.AsSpan(otherStart, span.Length), StringComparison.Ordinal) != 0)
                {
                    return false;
                }
                otherStart += span.Length;
            }

            ErrorUtilities.ThrowInternalError("Inconsistent {0} state", nameof(InternableString));
            throw new InvalidOperationException();
        }

        /// <summary>
        /// Returns a System.String representing this string. Allocates memory unless this InternableString was created by wrapping a
        /// System.String in which case the original string is returned.
        /// </summary>
        /// <returns>The string.</returns>
        internal unsafe string ExpensiveConvertToString()
        {
            if (Length == 0)
            {
                return string.Empty;
            }

            // Special case: if we hold just one string, we can directly return it.
            if (_inlineSpan.Length == Length)
            {
#if NETCOREAPP
                if (_inlineSpanString != null)
                {
                    return _inlineSpanString;
                }
#else
                return _inlineSpan.ToString();
#endif
            }
            if (_inlineSpan.IsEmpty && _spans[0].Length == Length)
            {
                return _spans[0].ToString();
            }

            // In all other cases we create a new string instance and concatenate all spans into it. Note that while technically mutating
            // the System.String, the technique is generally considered safe as we are the sole owners of the new object. It is important
            // to initialize the string with the '\0' characters as this hits an optimized code path in the runtime.
            string result = new string((char)0, Length);
            int charsRemaining = Length;

            fixed (char* resultPtr = result)
            {
                char* destPtr = resultPtr;
                if (_inlineSpan.Length > 0)
                {
                    _inlineSpan.CopyTo(new Span<char>(destPtr, charsRemaining));
                    destPtr += _inlineSpan.Length;
                    charsRemaining -= _inlineSpan.Length;
                }

                if (_spans != null)
                {
                    foreach (ReadOnlyMemory<char> span in _spans)
                    {
                        span.Span.CopyTo(new Span<char>(destPtr, charsRemaining));
                        destPtr += span.Length;
                        charsRemaining -= span.Length;
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Returns true if this InternableString wraps a System.String and the same System.String is passed as the argument.
        /// </summary>
        /// <param name="str">The string to compare to.</param>
        /// <returns>True is this instance wraps the given string.</returns>
        public bool ReferenceEquals(string str)
        {
            if (_inlineSpan.Length == Length)
            {
                return _inlineSpan == str.AsSpan();
            }
            if (_inlineSpan.IsEmpty && _spans.Count == 1 && _spans[0].Length == Length)
            {
                return _spans[0].Span == str.AsSpan();
            }
            return false;
        }

        /// <summary>
        /// Converts this instance to a System.String while first searching for a match in the intern table.
        /// </summary>
        /// <remarks>
        /// May allocate depending on whether the string has already been interned.
        /// </remarks>
        public override unsafe string ToString()
        {
            return OpportunisticIntern.Instance.InternableToString(ref this);
        }

        /// <summary>
        /// Releases this instance.
        /// </summary>
        public void Dispose()
        {
            _inlineSpan = default(ReadOnlySpan<char>);
            if (_spans != null)
            {
                s_arrayListPool.Return(_spans);
                _spans = null;
            }
            Length = 0;
#if NETCOREAPP
            _inlineSpanString = null;
#endif
        }

        #region Mutating public methods

        /// <summary>
        /// Appends a string.
        /// </summary>
        /// <param name="value">The string to append.</param>
        public void Append(string value)
        {
            AddSpan(value.AsMemory());
        }

        /// <summary>
        /// Appends a substring.
        /// </summary>
        /// <param name="value">The string to append.</param>
        /// <param name="startIndex">The start index of the substring within <paramref name="value"/> to append.</param>
        /// <param name="count">The length of the substring to append.</param>
        public void Append(string value, int startIndex, int count)
        {
            AddSpan(value.AsMemory(startIndex, count));
        }

        /// <summary>
        /// Removes leading white-space characters from the string.
        /// </summary>
        public void TrimStart()
        {
            int oldLength = _inlineSpan.Length;
            _inlineSpan = _inlineSpan.TrimStart();
            if (_inlineSpan.Length != oldLength)
            {
                Length -= (oldLength - _inlineSpan.Length);
#if NETCOREAPP
                _inlineSpanString = null;
#endif
            }

            if (_inlineSpan.IsEmpty && _spans != null)
            {
                for (int spanIdx = 0; spanIdx < _spans.Count; spanIdx++)
                {
                    ReadOnlySpan<char> span = _spans[spanIdx].Span;
                    int i = 0;
                    while (i < span.Length && char.IsWhiteSpace(span[i]))
                    {
                        i++;
                    }
                    if (i > 0)
                    {
                        _spans[spanIdx] = _spans[spanIdx].Slice(i);
                        Length -= i;
                    }
                    if (!_spans[spanIdx].IsEmpty)
                    {
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// Removes trailing white-space characters from the string.
        /// </summary>
        public void TrimEnd()
        {
            if (_spans != null)
            {
                for (int spanIdx = _spans.Count - 1; spanIdx >= 0; spanIdx--)
                {
                    ReadOnlySpan<char> span = _spans[spanIdx].Span;
                    int i = span.Length - 1;
                    while (i >= 0 && char.IsWhiteSpace(span[i]))
                    {
                        i--;
                    }
                    if (i + 1 < span.Length)
                    {
                        _spans[spanIdx] = _spans[spanIdx].Slice(0, i + 1);
                        Length -= span.Length - (i + 1);
                    }
                    if (!_spans[spanIdx].IsEmpty)
                    {
                        return;
                    }
                }
            }

            int oldLength = _inlineSpan.Length;
            _inlineSpan = _inlineSpan.TrimEnd();
            if (_inlineSpan.Length != oldLength)
            {
                Length -= (oldLength - _inlineSpan.Length);
#if NETCOREAPP
                _inlineSpanString = null;
#endif
            }
        }

        /// <summary>
        /// Removes leading and trailing white-space characters from the string.
        /// </summary>
        public void Trim()
        {
            TrimStart();
            TrimEnd();
        }

        /// <summary>
        /// Clears this instance making it represent an empty string.
        /// </summary>
        public void Clear()
        {
            _inlineSpan = default(ReadOnlySpan<char>);
            _spans?.Clear();
            Length = 0;
#if NETCOREAPP
            _inlineSpanString = null;
#endif
        }

        #endregion

        /// <summary>
        /// Appends a ReadOnlyMemory&lt;char&gt; span to the string.
        /// </summary>
        /// <param name="span"></param>
        private void AddSpan(ReadOnlyMemory<char> span)
        {
            _spans ??= s_arrayListPool.Get();
            _spans.Add(span);
            Length += span.Length;
        }
    }
}
