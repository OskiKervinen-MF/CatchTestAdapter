﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Adapter;
using Microsoft.VisualStudio.TestWindow;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Xml.XPath;
using System.Globalization;

namespace CatchTestAdapter
{
    [ExtensionUri(ExecutorUriString)]
    public class TestExecutor : ITestExecutor
    {
        public const string ExecutorUriString = "executor://CatchTestRunner/v1";
        public static readonly Uri ExecutorUri = new Uri(ExecutorUriString);
        private string exe;

        public void Cancel()
        {
            throw new NotImplementedException();
        }

        public void RunTests(IEnumerable<string> sources, IRunContext runContext, IFrameworkHandle frameworkHandle)
        {
            frameworkHandle.SendMessage(TestMessageLevel.Informational, "RunTest with source " + sources.First());
            foreach(var exeName in sources)
            {
                var  tests = TestDiscoverer.CreateTestCases(exeName);
                RunTests(tests, runContext, frameworkHandle);
            }
        }

        /// <summary>
        /// Describes the result of an expression, with the section path flattened to a string.
        /// </summary>
        struct FlatResult
        {
            public string SectionPath;
            public string Expression;
            public int LineNumber;
            public string FilePath;
        }

        /// <summary>
        /// Tries to find a failure in the section tree of a test case.
        /// </summary>
        /// <param name="element"></param>
        /// <param name="result"></param>
        /// <returns></returns>
        bool TryGetFailure( XElement element, out FlatResult result )
        {
            // Parse test cases and their sections.
            if( element.Name == "Section" || element.Name == "TestCase" )
            {
                // Get current level's name.
                string name = element.Attribute( "name" ).Value;

                // Try to find the failure from this element.
                foreach( var expression in element.Elements("Expression") )
                {
                    // Map the failure to a flat result.
                    if ( expression.Attribute( "success" ).Value == "false" )
                    {
                        string expanded = expression.Element( "Expanded" ).Value;
                        string original = expression.Element( "Original" ).Value;
                        string type = expression.Attribute( "type" ).Value;
                        result = new FlatResult() {
                            // The path will be expanced by preciding stack frames.
                            SectionPath = name,
                            Expression = String.Format( CultureInfo.InvariantCulture,
                                "{0} {1} => {2}", type, original, expanded),
                            LineNumber = Int32.Parse( expression.Attribute("line").Value ),
                            FilePath = expression.Attribute("filename").Value
                        };
                        return true;
                    }
                }

                // Try to find the failure from a subsection of this element.
                foreach( var section in element.Elements("Section") )
                {
                    // Try to find a failure in this section.
                    if( TryGetFailure( section, out result ) )
                    {
                        // If found, add the current section to the path and return it.
                        result.SectionPath = name + "\n" + result.SectionPath;
                        return true;
                    }
                }
            }

            // Return a dummy result if no failure found.
            result = new FlatResult() {
                SectionPath = "[Not found]",
                Expression = "N/A",
                LineNumber = -1,
                FilePath = "" };
            return false;
        }

        /// <summary>
        /// Finds a failure in a test case and flattens the section path that leads to it.
        /// </summary>
        /// <param name="testCase"></param>
        /// <returns></returns>
        FlatResult GetFlatFailure( XElement testCase )
        {
            FlatResult result;
            if ( TryGetFailure( testCase, out result ) )
                return result;
            else
                throw new Exception( "Could not find failure " + testCase.ToString() );
        }

        public void RunTests(IEnumerable<TestCase> tests, IRunContext runContext, IFrameworkHandle frameworkHandle)
        {
            frameworkHandle.SendMessage(TestMessageLevel.Informational, "RunTest with test cases " + tests);
            foreach (var test in tests)
            {
                frameworkHandle.SendMessage(TestMessageLevel.Informational, test.DisplayName);
                var p = new ProcessRunner(test.Source, "-r xml \"" + test.DisplayName + "\"");

                // Output as a single string.
                string output = p.Output.Aggregate( "", ( acc, add ) => acc + add );

                // Output as an XML document.
                XDocument doc = XDocument.Parse( output );

                // Process the output.
                var testResult = new TestResult( test );
                foreach ( var group in doc.Element("Catch").Elements("Group") )
                {
                    foreach( var testCase in group.Elements( "TestCase" ) )
                    {
                        XElement result = testCase.Element( "OverallResult" );
                        if( result.Attribute("success" ).Value.ToLowerInvariant() == "true" )
                        {
                            testResult.Outcome = TestOutcome.Passed;
                        }
                        else
                        {
                            // Mark failure.
                            testResult.Outcome = TestOutcome.Failed;

                            // Parse the failure to a flat result.
                            FlatResult failure = GetFlatFailure( testCase );

                            // Populate the test result.
                            testResult.ErrorMessage = failure.SectionPath + "\n" + failure.Expression;
                            testResult.ErrorStackTrace = String.Format( CultureInfo.InvariantCulture, "at {0}() in {1}:line {2}\n",
                                test.DisplayName,
                                failure.FilePath,
                                failure.LineNumber );
                        }
                    }
                }
                frameworkHandle.RecordResult(testResult);
            }
        }
    }
}