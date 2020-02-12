using System;
using System.Collections.Generic;

namespace Microsoft.VisualStudio.Services.Agent.Listener.Diagnostics
{
    public class DiagnosticTests
    {
        public DiagnosticTests(ITerminal terminal)
        {
            m_terminal = terminal;

            m_diagnosticTests = new List<IDiagnosticTest>()
            {
                new DnsTest(),
                new PingTest(),
            };

            m_diagnosticInfo = new List<IDiagnosticInfo>()
            {
                new MtuInfo()
            };
        }

        public void Execute()
        {
            bool result = true;
            foreach(var test in m_diagnosticTests)
            {
                string testName = test.GetType().Name;
                m_terminal.WriteLine(string.Format("*** Performing Test: {0} ***", testName));
                try
                {
                    if (!test.Execute(m_terminal))
                    {
                        result = false;
                        m_terminal.WriteError(string.Format("*** Test: {0} Failed ***", testName));
                    }
                    else
                    {
                        m_terminal.WriteLine(string.Format("*** Test: {0} Succeeded ***", testName));
                    }
                }
                catch (Exception ex)
                {
                    result = false;
                    m_terminal.WriteError(ex);
                    m_terminal.WriteError(string.Format("*** Test: {0} Failed ***", testName));
                }
                m_terminal.WriteLine(string.Empty);
            }

            if (result)
            {
                m_terminal.WriteLine("Diagnostics tests were successful!");
            }
            else
            {
                m_terminal.WriteLine("1 or more diagnostics tests FAILED!");
            }
            m_terminal.WriteLine(string.Empty);
            m_terminal.WriteLine(string.Empty);
            m_terminal.WriteLine(string.Empty);


            m_terminal.WriteLine("--- Additional Information ---");
            foreach (var info in m_diagnosticInfo)
            {
                string infoName = info.GetType().Name;
                m_terminal.WriteLine(string.Format("*** Info: {0} ***", infoName));
                try
                {
                    info.Execute(m_terminal);
                }
                catch (Exception ex)
                {
                    m_terminal.WriteError(ex);
                    m_terminal.WriteError(string.Format("*** Info: {0} Failed ***", infoName));
                }
                m_terminal.WriteLine(string.Empty);
            }
        }

        private List<IDiagnosticInfo> m_diagnosticInfo;
        private List<IDiagnosticTest> m_diagnosticTests;
        private ITerminal m_terminal;
    }
}
