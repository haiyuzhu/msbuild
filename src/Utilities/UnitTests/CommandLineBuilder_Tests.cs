﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.IO;

using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Utilities;

using NUnit.Framework;

namespace Microsoft.Build.UnitTests
{
    [TestFixture]
    sealed public class CommandLineBuilderTest
    {
        /*
        * Method:   AppendSwitchSimple
        *
        * Just append a simple switch.
        */
        [Test]
        public void AppendSwitchSimple()
        {
            CommandLineBuilder c = new CommandLineBuilder();
            c.AppendSwitch("/a");
            c.AppendSwitch("-b");
            Assert.AreEqual(
                CommandLineBuilder.FixCommandLineSwitch("/a ") + CommandLineBuilder.FixCommandLineSwitch("-b"),
                c.ToString());
        }

        /*
        * Method:   AppendSwitchWithStringParameter
        *
        * Append a switch that has a string parameter.
        */
        [Test]
        public void AppendSwitchWithStringParameter()
        {
            CommandLineBuilder c = new CommandLineBuilder();
            c.AppendSwitchIfNotNull("/animal:", "dog");
            Assert.AreEqual(CommandLineBuilder.FixCommandLineSwitch("/animal:dog"), c.ToString());
        }

        /*
        * Method:   AppendSwitchWithSpacesInParameter
        *
        * This should trigger implicit quoting.
        */
        [Test]
        public void AppendSwitchWithSpacesInParameter()
        {
            CommandLineBuilder c = new CommandLineBuilder();
            c.AppendSwitchIfNotNull("/animal:", "dog and pony");
            Assert.AreEqual(CommandLineBuilder.FixCommandLineSwitch("/animal:\"dog and pony\""), c.ToString());
        }

        /// <summary>
        /// Test for AppendSwitchIfNotNull for the ITaskItem version
        /// </summary>
        [Test]
        public void AppendSwitchWithSpacesInParameterTaskItem()
        {
            CommandLineBuilder c = new CommandLineBuilder();
            c.AppendSwitchIfNotNull("/animal:", (ITaskItem)new TaskItem("dog and pony"));
            Assert.AreEqual(CommandLineBuilder.FixCommandLineSwitch("/animal:\"dog and pony\""), c.ToString());
        }

        /*
        * Method:   AppendLiteralSwitchWithSpacesInParameter
        *
        * Implicit quoting should not happen.
        */
        [Test]
        public void AppendLiteralSwitchWithSpacesInParameter()
        {
            CommandLineBuilder c = new CommandLineBuilder();
            c.AppendSwitchUnquotedIfNotNull("/animal:", "dog and pony");
            Assert.AreEqual(CommandLineBuilder.FixCommandLineSwitch("/animal:dog and pony"), c.ToString());
        }

        /*
        * Method:   AppendTwoStringsEnsureNoSpace
        *
        * When appending two comma-delimted strings, there should be no space before the comma.
        */
        [Test]
        public void AppendTwoStringsEnsureNoSpace()
        {
            CommandLineBuilder c = new CommandLineBuilder();
            c.AppendFileNamesIfNotNull(new string[] { "Form1.resx", FileUtilities.FixFilePath("built\\Form1.resources") }, ",");

            // There shouldn't be a space before or after the comma
            // Tools like resgen require comma-delimited lists to be bumped up next to each other.
            Assert.AreEqual(FileUtilities.FixFilePath(@"Form1.resx,built\Form1.resources"), c.ToString());
        }

        /*
        * Method:   AppendSourcesArray
        *
        * Append several sources files using JoinAppend
        */
        [Test]
        public void AppendSourcesArray()
        {
            CommandLineBuilder c = new CommandLineBuilder();
            c.AppendFileNamesIfNotNull(new string[] { "Mercury.cs", "Venus.cs", "Earth.cs" }, " ");

            // Managed compilers use this function to append sources files.
            Assert.AreEqual(@"Mercury.cs Venus.cs Earth.cs", c.ToString());
        }

        /*
        * Method:   AppendSourcesArrayWithDashes
        *
        * Append several sources files starting with dashes using JoinAppend
        */
        [Test]
        public void AppendSourcesArrayWithDashes()
        {
            CommandLineBuilder c = new CommandLineBuilder();
            c.AppendFileNamesIfNotNull(new string[] { "-Mercury.cs", "-Venus.cs", "-Earth.cs" }, " ");

            // Managed compilers use this function to append sources files.
            Assert.AreEqual("." + Path.DirectorySeparatorChar + "-Mercury.cs ." +
                Path.DirectorySeparatorChar + "-Venus.cs ." +
                Path.DirectorySeparatorChar + "-Earth.cs", c.ToString());
        }

        /// <summary>
        /// Test AppendFileNamesIfNotNull, the ITaskItem version
        /// </summary>
        [Test]
        public void AppendSourcesArrayWithDashesTaskItem()
        {
            CommandLineBuilder c = new CommandLineBuilder();
            c.AppendFileNamesIfNotNull(new TaskItem[] { new TaskItem("-Mercury.cs"), null, new TaskItem("Venus.cs"), new TaskItem("-Earth.cs") }, " ");

            // Managed compilers use this function to append sources files.
            Assert.AreEqual("." + Path.DirectorySeparatorChar + "-Mercury.cs  Venus.cs ." + Path.DirectorySeparatorChar + "-Earth.cs", c.ToString());
        }

        /*
        * Method:   JoinAppendEmpty
        *
        * Append append and empty array. Result should be NOP.
        */
        [Test]
        public void JoinAppendEmpty()
        {
            CommandLineBuilder c = new CommandLineBuilder();
            c.AppendFileNamesIfNotNull(new string[] { "" }, " ");

            // Managed compilers use this function to append sources files.
            Assert.AreEqual(@"", c.ToString());
        }

        /*
        * Method:   JoinAppendNull
        *
        * Append append and empty array. Result should be NOP.
        */
        [Test]
        public void JoinAppendNull()
        {
            CommandLineBuilder c = new CommandLineBuilder();
            c.AppendFileNamesIfNotNull((string[])null, " ");

            // Managed compilers use this function to append sources files.
            Assert.AreEqual(@"", c.ToString());
        }

        /// <summary>
        /// Append a switch with parameter array, quoting
        /// </summary>
        [Test]
        public void AppendSwitchWithParameterArrayQuoting()
        {
            CommandLineBuilder c = new CommandLineBuilder();
            c.AppendSwitch("/something");
            c.AppendSwitchIfNotNull("/switch:", new string[] { "Mer cury.cs", "Ve nus.cs", "Ear th.cs" }, ",");

            // Managed compilers use this function to append sources files.
            Assert.AreEqual(
                CommandLineBuilder.FixCommandLineSwitch("/something ")
                + CommandLineBuilder.FixCommandLineSwitch("/switch:\"Mer cury.cs\",\"Ve nus.cs\",\"Ear th.cs\""),
                c.ToString());
        }

        /// <summary>
        /// Append a switch with parameter array, quoting, ITaskItem version
        /// </summary>
        [Test]
        public void AppendSwitchWithParameterArrayQuotingTaskItem()
        {
            CommandLineBuilder c = new CommandLineBuilder();
            c.AppendSwitch("/something");
            c.AppendSwitchIfNotNull("/switch:", new TaskItem[] { new TaskItem("Mer cury.cs"), null, new TaskItem("Ve nus.cs"), new TaskItem("Ear th.cs") }, ",");

            // Managed compilers use this function to append sources files.
            Assert.AreEqual(
                CommandLineBuilder.FixCommandLineSwitch("/something ")
                + CommandLineBuilder.FixCommandLineSwitch("/switch:\"Mer cury.cs\",,\"Ve nus.cs\",\"Ear th.cs\""),
                c.ToString());
        }

        /// <summary>
        /// Append a switch with parameter array, no quoting
        /// </summary>
        [Test]
        public void AppendSwitchWithParameterArrayNoQuoting()
        {
            CommandLineBuilder c = new CommandLineBuilder();
            c.AppendSwitch("/something");
            c.AppendSwitchUnquotedIfNotNull("/switch:", new string[] { "Mer cury.cs", "Ve nus.cs", "Ear th.cs" }, ",");

            // Managed compilers use this function to append sources files.
            Assert.AreEqual(
                CommandLineBuilder.FixCommandLineSwitch("/something ")
                + CommandLineBuilder.FixCommandLineSwitch("/switch:Mer cury.cs,Ve nus.cs,Ear th.cs"),
                c.ToString());
        }

        /// <summary>
        /// Append a switch with parameter array, no quoting, ITaskItem version
        /// </summary>
        [Test]
        public void AppendSwitchWithParameterArrayNoQuotingTaskItem()
        {
            CommandLineBuilder c = new CommandLineBuilder();
            c.AppendSwitch("/something");
            c.AppendSwitchUnquotedIfNotNull("/switch:", new TaskItem[] { new TaskItem("Mer cury.cs"), null, new TaskItem("Ve nus.cs"), new TaskItem("Ear th.cs") }, ",");

            // Managed compilers use this function to append sources files.
            Assert.AreEqual(
                CommandLineBuilder.FixCommandLineSwitch("/something ")
                + CommandLineBuilder.FixCommandLineSwitch("/switch:Mer cury.cs,,Ve nus.cs,Ear th.cs"),
                c.ToString());
        }

        /// <summary>
        /// Appends a single file name
        /// </summary>
        [Test]
        public void AppendSingleFileName()
        {
            CommandLineBuilder c = new CommandLineBuilder();
            c.AppendSwitch("/something");
            c.AppendFileNameIfNotNull("-Mercury.cs");
            c.AppendFileNameIfNotNull("Mercury.cs");
            c.AppendFileNameIfNotNull("Mer cury.cs");

            // Managed compilers use this function to append sources files.
            Assert.AreEqual(
                CommandLineBuilder.FixCommandLineSwitch("/something ." + Path.DirectorySeparatorChar + "-Mercury.cs Mercury.cs \"Mer cury.cs\""),
                c.ToString());
        }

        /// <summary>
        /// Appends a single file name, ITaskItem version
        /// </summary>
        [Test]
        public void AppendSingleFileNameTaskItem()
        {
            CommandLineBuilder c = new CommandLineBuilder();
            c.AppendSwitch("/something");
            c.AppendFileNameIfNotNull((ITaskItem)new TaskItem("-Mercury.cs"));
            c.AppendFileNameIfNotNull((ITaskItem)new TaskItem("Mercury.cs"));
            c.AppendFileNameIfNotNull((ITaskItem)new TaskItem("Mer cury.cs"));

            // Managed compilers use this function to append sources files.
            Assert.AreEqual(
                CommandLineBuilder.FixCommandLineSwitch("/something ." + Path.DirectorySeparatorChar + "-Mercury.cs Mercury.cs \"Mer cury.cs\""),
                c.ToString());
        }

        /// <summary>
        /// Verify that we throw an exception correctly for the case where we don't have a switch name
        /// </summary>
        [Test]
        [ExpectedException(typeof(ArgumentException))]
        public void AppendSingleFileNameWithQuotes()
        {
            // Cannot have escaped quotes in a file name
            CommandLineBuilder c = new CommandLineBuilder();
            c.AppendFileNameIfNotNull("string with \"quotes\"");

            Assert.AreEqual("\"string with \\\"quotes\\\"\"", c.ToString());
        }

        /// <summary>
        /// Trigger escaping of literal quotes.
        /// </summary>
        [Test]
        public void AppendSwitchWithLiteralQuotesInParameter()
        {
            CommandLineBuilder c = new CommandLineBuilder();
            c.AppendSwitchIfNotNull("/D", "LSYSTEM_COMPATIBLE_ASSEMBLY_NAME=L\"Microsoft.Windows.SystemCompatible\"");
            Assert.AreEqual(
                CommandLineBuilder.FixCommandLineSwitch(
                    "/D\"LSYSTEM_COMPATIBLE_ASSEMBLY_NAME=L\\\"Microsoft.Windows.SystemCompatible\\\"\""),
                c.ToString());
        }

        /// <summary>
        /// Trigger escaping of literal quotes.
        /// </summary>
        [Test]
        public void AppendSwitchWithLiteralQuotesInParameter2()
        {
            CommandLineBuilder c = new CommandLineBuilder();
            c.AppendSwitchIfNotNull("/D", @"ASSEMBLY_KEY_FILE=""c:\\foo\\FinalKeyFile.snk""");
            Assert.AreEqual(
                CommandLineBuilder.FixCommandLineSwitch(@"/D""ASSEMBLY_KEY_FILE=\""c:\\foo\\FinalKeyFile.snk\"""""),
                c.ToString());
        }

        /// <summary>
        /// Trigger escaping of literal quotes. This time, a double set of literal quotes.
        /// </summary>
        [Test]
        public void AppendSwitchWithLiteralQuotesInParameter3()
        {
            CommandLineBuilder c = new CommandLineBuilder();
            c.AppendSwitchIfNotNull("/D", @"""A B"" and ""C""");
            Assert.AreEqual(CommandLineBuilder.FixCommandLineSwitch(@"/D""\""A B\"" and \""C\"""""), c.ToString());
        }

        /// <summary>
        /// When a value contains a backslash, it doesn't normally need escaping.
        /// </summary>
        [Test]
        public void AppendQuotableSwitchContainingBackslash()
        {
            CommandLineBuilder c = new CommandLineBuilder();
            c.AppendSwitchIfNotNull("/D", @"A \B");
            Assert.AreEqual(CommandLineBuilder.FixCommandLineSwitch(@"/D""A \B"""), c.ToString());
        }

        /// <summary>
        /// Backslashes before quotes need escaping themselves.
        /// </summary>
        [Test]
        public void AppendQuotableSwitchContainingBackslashBeforeLiteralQuote()
        {
            CommandLineBuilder c = new CommandLineBuilder();
            c.AppendSwitchIfNotNull("/D", @"A"" \""B");
            Assert.AreEqual(CommandLineBuilder.FixCommandLineSwitch(@"/D""A\"" \\\""B"""), c.ToString());
        }

        /// <summary>
        /// Don't quote if not asked to
        /// </summary>
        [Test]
        public void AppendSwitchUnquotedIfNotNull()
        {
            CommandLineBuilder c = new CommandLineBuilder();
            c.AppendSwitchUnquotedIfNotNull("/D", @"A"" \""B");
            Assert.AreEqual(CommandLineBuilder.FixCommandLineSwitch(@"/DA"" \""B"), c.ToString());
        }

        /// <summary>
        /// When a value ends with a backslash, that certainly should be escaped if it's
        /// going to be quoted.
        /// </summary>
        [Test]
        public void AppendQuotableSwitchEndingInBackslash()
        {
            CommandLineBuilder c = new CommandLineBuilder();
            c.AppendSwitchIfNotNull("/D", @"A B\");
            Assert.AreEqual(CommandLineBuilder.FixCommandLineSwitch(@"/D""A B\\"""), c.ToString());
        }

        /// <summary>
        /// Backslashes don't need to be escaped if the string isn't going to get quoted.
        /// </summary>
        [Test]
        public void AppendNonQuotableSwitchEndingInBackslash()
        {
            CommandLineBuilder c = new CommandLineBuilder();
            c.AppendSwitchIfNotNull("/D", @"AB\");
            Assert.AreEqual(CommandLineBuilder.FixCommandLineSwitch(@"/DAB\"), c.ToString());
        }

        /// <summary>
        /// Quoting of hyphens
        /// </summary>
        [Test]
        public void AppendQuotableSwitchWithHyphen()
        {
            CommandLineBuilder c = new CommandLineBuilder(/* do not quote hyphens*/);
            c.AppendSwitchIfNotNull("/D", @"foo-bar");
            Assert.AreEqual(CommandLineBuilder.FixCommandLineSwitch(@"/Dfoo-bar"), c.ToString());
        }

        /// <summary>
        /// Quoting of hyphens 2
        /// </summary>
        [Test]
        public void AppendQuotableSwitchWithHyphenQuoting()
        {
            CommandLineBuilder c = new CommandLineBuilder(true /* quote hyphens*/);
            c.AppendSwitchIfNotNull("/D", @"foo-bar");
            Assert.AreEqual(CommandLineBuilder.FixCommandLineSwitch(@"/D""foo-bar"""), c.ToString());
        }

        /// <summary>
        /// Appends an ITaskItem item spec as a parameter
        /// </summary>
        [Test]
        public void AppendSwitchTaskItem()
        {
            CommandLineBuilder c = new CommandLineBuilder(true);
            c.AppendSwitchIfNotNull("/D", new TaskItem(@"foo-bar"));
            Assert.AreEqual(CommandLineBuilder.FixCommandLineSwitch(@"/D""foo-bar"""), c.ToString());
        }

        /// <summary>
        /// Appends an ITaskItem item spec as a parameter
        /// </summary>
        [Test]
        public void AppendSwitchUnQuotedTaskItem()
        {
            CommandLineBuilder c = new CommandLineBuilder(true);
            c.AppendSwitchUnquotedIfNotNull("/D", new TaskItem(@"foo-bar"));
            Assert.AreEqual(CommandLineBuilder.FixCommandLineSwitch(@"/Dfoo-bar"), c.ToString());
        }

        /// <summary>
        /// Odd number of literal quotes. This should trigger an exception, because command line parsers
        /// generally can't handle this case.
        /// </summary>
        [Test]
        [ExpectedException(typeof(ArgumentException))]
        public void AppendSwitchWithOddNumberOfLiteralQuotesInParameter()
        {
            CommandLineBuilder c = new CommandLineBuilder();
            c.AppendSwitchIfNotNull("/D", @"ASSEMBLY_KEY_FILE=""c:\\foo\\FinalKeyFile.snk");
        }

        internal class TestCommandLineBuilder : CommandLineBuilder
        {
            internal void TestVerifyThrow(string switchName, string parameter)
            {
                VerifyThrowNoEmbeddedDoubleQuotes(switchName, parameter);
            }

            protected override void VerifyThrowNoEmbeddedDoubleQuotes(string switchName, string parameter)
            {
                base.VerifyThrowNoEmbeddedDoubleQuotes(switchName, parameter);
            }
        }

        /// <summary>
        /// Test the else of VerifyThrowNOEmbeddedDouble quotes where the switch name is not empty or null
        /// </summary>
        [Test]
        [ExpectedException(typeof(ArgumentException))]
        public void TestVerifyThrowElse()
        {
            TestCommandLineBuilder c = new TestCommandLineBuilder();
            c.TestVerifyThrow("SuperSwitch", @"Parameter");
            c.TestVerifyThrow("SuperSwitch", @"Para""meter");
        }
    }
}
