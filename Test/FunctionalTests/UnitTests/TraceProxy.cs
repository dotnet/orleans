using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;

namespace UnitTests
{
    public class TraceProxy : System.MarshalByRefObject
    {
        public TraceProxy()
        {
            if (AppDomain.CurrentDomain.SetupInformation.AppDomainInitializerArguments != null && AppDomain.CurrentDomain.SetupInformation.AppDomainInitializerArguments.Length > 0)
            {
                ConfigureTraceListeners(AppDomain.CurrentDomain.SetupInformation.AppDomainInitializerArguments[0]);
            }
        }
        
        public void ConfigureTraceListeners(string logFilePath)
        {
            try
            {
                TextWriterTraceListener textListener = null;
                string path = System.IO.Path.GetDirectoryName(logFilePath);
                if (!System.IO.Directory.Exists(path))
                {
                    System.IO.Directory.CreateDirectory(path);
                }


                foreach (TraceListener listener in Trace.Listeners)
                {
                    if (listener is TextWriterTraceListener)
                    {
                        listener.Flush();
                        listener.Close();
                        textListener = listener as TextWriterTraceListener;
                        break;
                    }
                }

                if (textListener == null)
                {
                    textListener = new System.Diagnostics.TextWriterTraceListener(logFilePath);
                    Trace.Listeners.Add(textListener);
                }

                System.IO.StreamWriter sw = null;
                try
                {
                    sw =  new System.IO.StreamWriter(logFilePath, true);
                }
                catch (Exception)
                {
                    string fileName = System.IO.Path.GetFileName(logFilePath);
                    path = System.IO.Path.Combine(new string[] { path, Guid.NewGuid().ToString() + fileName });
                    sw = new System.IO.StreamWriter(path, true);
                }
                finally
                {
                    textListener.Writer = sw;
                    System.Console.SetOut(textListener.Writer);
                }
            }
            catch (Exception)
            {
            }        
        }
        
    }
}
