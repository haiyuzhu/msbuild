﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//-----------------------------------------------------------------------
// </copyright>
// <summary>Unit tests for BuildWarningEventArgs</summary>
//-----------------------------------------------------------------------

using System;

using Microsoft.Build.Framework;
using NUnit.Framework;
#pragma warning disable 0219

namespace Microsoft.Build.UnitTests
{
    /// <summary>
    /// Verify the functioning of the BuildWarningEventArgs class.
    /// </summary>
    [TestFixture]
    public class BuildWarningEventArgs_Tests
    {
        /// <summary>
        /// Default event to use in tests.
        /// </summary>
        private BuildWarningEventArgs _baseWarningEvent = new BuildWarningEventArgs("Subcategory", "Code", "File", 1, 2, 3, 4, "Message", "HelpKeyword", "sender");

        /// <summary>
        /// Trivially exercise event args default ctors to boost Frameworks code coverage
        /// </summary>
        [Test]
        public void EventArgsCtors()
        {
            BuildWarningEventArgs buildWarningEvent = new BuildWarningEventArgs2();
            buildWarningEvent = new BuildWarningEventArgs("Subcategory", "Code", "File", 1, 2, 3, 4, "Message", "HelpKeyword", "sender");
            buildWarningEvent = new BuildWarningEventArgs("Subcategory", "Code", "File", 1, 2, 3, 4, "Message", "HelpKeyword", "sender", DateTime.Now);
            buildWarningEvent = new BuildWarningEventArgs("Subcategory", "Code", "File", 1, 2, 3, 4, "{0}", "HelpKeyword", "sender", DateTime.Now, "Message");
            buildWarningEvent = new BuildWarningEventArgs(null, null, null, 1, 2, 3, 4, null, null, null);
            buildWarningEvent = new BuildWarningEventArgs(null, null, null, 1, 2, 3, 4, null, null, null, DateTime.Now);
            buildWarningEvent = new BuildWarningEventArgs(null, null, null, 1, 2, 3, 4, null, null, null, DateTime.Now, null);
        }

        /// <summary>
        /// Trivially exercise getHashCode.
        /// </summary>
        [Test]
        public void TestGetHashCode()
        {
            _baseWarningEvent.GetHashCode();
        }

        /// <summary>
        /// Create a derrived class so that we can test the default constructor in order to increase code coverage and 
        /// verify this code path does not cause any exceptions.
        /// </summary>
        private class BuildWarningEventArgs2 : BuildWarningEventArgs
        {
            /// <summary>
            /// Default constructor
            /// </summary>
            public BuildWarningEventArgs2()
                : base()
            {
            }
        }
    }
}