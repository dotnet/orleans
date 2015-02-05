using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Orleans.TestFramework.SelfTest
{
    [TestClass]
    public class QuickParserTests
    {
        [TestMethod]
        public void TestFrameworkTest_SimpleParser()
        {
            ParserGrammar grammar = new ParserGrammar();
            grammar.AddTransitionPattern("R1", "Started", "State1", "MyPattern1", 1);
            grammar.AddTransitionPattern("R2", "State1", "State2", "MyPattern2", 2);
            
            QuickParser parser = new QuickParser(grammar);

            FileSimulator sim = new FileSimulator();
            parser.BeginAnalysis("Dummy", sim);
            Assert.IsTrue(parser.CurrentState == "Initialized");
            sim.AddLines(
                "Random",
                "Non-matching",
                "lines"
                );
            sim.Fire();
            Assert.IsTrue(parser.CurrentState == "Started");
            sim.AddLine("MyPattern1");
            sim.Fire();
            Assert.IsTrue(parser.CurrentState == "State1");
            sim.AddLine("MyPattern1");
            sim.Fire();
            Assert.IsTrue(parser.CurrentState == "State1");
            sim.AddLine("MyPattern2");
            sim.Fire();
            Assert.IsTrue(parser.CurrentState == "State1");
            sim.AddLine("MyPattern2");
            sim.Fire();
            Assert.IsTrue(parser.CurrentState == "State2");
        }

        [TestMethod]
        public void TestFrameworkTest_SimulatedRestart()
        {
            ParserGrammar grammar = new ParserGrammar();
            grammar.AddTransitionPattern("R1", "Started", "Printing", "Current TPS:", 1);
            grammar.AddTransitionPattern("R2", "Printing", "Stable", "Current TPS:", 3, consecutive: true);
            grammar.AddTransitionPattern("R2", "Unstable", "Stable", "Current TPS:", 3, consecutive: true);

            List<Tuple<string, string>> linesBefore = new List<Tuple<string,string>>();
            List<Tuple<string, string>> linesCallbackExpected = new List<Tuple<string, string>>();
            linesBefore.Add(new Tuple<string, string>("Current TPS:1", "Printing"));
            linesBefore.Add(new Tuple<string, string>("Current TPS:2", "Printing"));
            linesBefore.Add(new Tuple<string, string>("Current TPS:3", "Printing"));
            linesBefore.Add(new Tuple<string, string>("Current TPS:4", "Stable"));
            linesBefore.Add(new Tuple<string, string>("Current TPS:5", "Stable"));
            linesBefore.Add(new Tuple<string, string>("Current TPS:6", "Stable"));
            linesBefore.Add(new Tuple<string, string>("Current TPS:7", "Stable"));

            List<Tuple<string, string>> linesAfter = new List<Tuple<string, string>>();
            linesAfter.Add(new Tuple<string, string>("Current TPS:8", "Unstable"));
            linesAfter.Add(new Tuple<string, string>("Exception", "Unstable"));
            linesAfter.Add(new Tuple<string, string>("Current TPS:9", "Unstable"));
            linesAfter.Add(new Tuple<string, string>("Current TPS:10", "Unstable"));
            linesAfter.Add(new Tuple<string, string>("Exception", "Unstable"));
            linesAfter.Add(new Tuple<string, string>("Exception", "Unstable"));
            linesAfter.Add(new Tuple<string, string>("Current TPS:11", "Unstable"));
            linesAfter.Add(new Tuple<string, string>("Current TPS:12", "Unstable"));
            linesAfter.Add(new Tuple<string, string>("Current TPS:13", "Stable")); //<===
            linesCallbackExpected.Add(new Tuple<string, string>("Current TPS:11","Current TPS:13"));
            linesAfter.Add(new Tuple<string, string>("Current TPS:14", "Stable"));
            linesAfter.Add(new Tuple<string, string>("Exception", "Stable"));
            linesAfter.Add(new Tuple<string, string>("Current TPS:15", "Stable"));


            List<Tuple<string, string>> linesAfter2 = new List<Tuple<string, string>>();
            linesAfter2.Add(new Tuple<string, string>("Current TPS:16", "Unstable"));
            linesAfter2.Add(new Tuple<string, string>("Exception", "Unstable"));
            linesAfter2.Add(new Tuple<string, string>("Current TPS:17", "Unstable"));
            linesAfter2.Add(new Tuple<string, string>("Current TPS:18", "Unstable"));
            linesAfter2.Add(new Tuple<string, string>("Exception", "Unstable"));
            linesAfter2.Add(new Tuple<string, string>("Exception", "Unstable"));
            linesAfter2.Add(new Tuple<string, string>("Current TPS:19", "Unstable"));
            linesAfter2.Add(new Tuple<string, string>("Current TPS:20", "Unstable"));
            linesAfter2.Add(new Tuple<string, string>("Current TPS:21", "Stable")); //<===
            linesCallbackExpected.Add(new Tuple<string, string>("Current TPS:19", "Current TPS:21"));
            linesAfter2.Add(new Tuple<string, string>("Current TPS:22", "Stable"));
            linesAfter2.Add(new Tuple<string, string>("Exception", "Stable"));
            linesAfter2.Add(new Tuple<string, string>("Current TPS:23", "Stable"));

            List<Tuple<string, string>> linesAfter3 = new List<Tuple<string, string>>();
            linesAfter3.Add(new Tuple<string, string>("Current TPS:24", "Unstable"));
            linesAfter3.Add(new Tuple<string, string>("Exception", "Unstable"));
            linesAfter3.Add(new Tuple<string, string>("Current TPS:25", "Unstable"));
            linesAfter3.Add(new Tuple<string, string>("Current TPS:26", "Unstable"));
            linesAfter3.Add(new Tuple<string, string>("Exception", "Unstable"));
            linesAfter3.Add(new Tuple<string, string>("Exception", "Unstable"));
            linesAfter3.Add(new Tuple<string, string>("Current TPS:27", "Unstable"));
            linesAfter3.Add(new Tuple<string, string>("Current TPS:28", "Unstable"));
            linesAfter3.Add(new Tuple<string, string>("Current TPS:29", "Stable")); //<===
            linesCallbackExpected.Add(new Tuple<string, string>("Current TPS:27", "Current TPS:29"));
            linesAfter3.Add(new Tuple<string, string>("Current TPS:30", "Stable"));
            linesAfter3.Add(new Tuple<string, string>("Exception", "Stable"));
            linesAfter3.Add(new Tuple<string, string>("Current TPS:31", "Stable"));

            QuickParser parser = new QuickParser(grammar);

            FileSimulator sim = new FileSimulator();
            parser.BeginAnalysis("Dummy", sim);
            
            Assert.IsTrue(parser.CurrentState == "Initialized");

            foreach (var t in linesBefore)
            {
                Console.WriteLine("{{{0}}} / {1} => {{{2}}}", parser.CurrentState, t.Item1, t.Item2);
                sim.AddLine(t.Item1);
                sim.Fire();
                Assert.IsTrue(parser.CurrentState == t.Item2);
                
            }
            List<Tuple<string, string>> linesCallbackActual = new List<Tuple<string, string>>();
            parser.TransitionCallbacks.Add("Stable", (qp, first, last) =>
            {
                Console.WriteLine("\t\t\tEvent fired First={0}  Last={1}", first, last);
                linesCallbackActual.Add(new Tuple<string, string>(first, last));
            });
            
            parser.ForceState("Unstable");
            Console.WriteLine("FORCING {{{0}}} ", parser.CurrentState);
            foreach (var t in linesAfter)
            {
                Console.WriteLine("{{{0}}} / {1} => {{{2}}}", parser.CurrentState, t.Item1, t.Item2);
                sim.AddLine(t.Item1);
                sim.Fire();
                Assert.IsTrue(parser.CurrentState == t.Item2);
            }

            parser.ForceState("Unstable");
            Console.WriteLine("FORCING {{{0}}} ", parser.CurrentState);
            foreach (var t in linesAfter2)
            {
                Console.WriteLine("{{{0}}} / {1} => {{{2}}}", parser.CurrentState, t.Item1, t.Item2);
                sim.AddLine(t.Item1);
                sim.Fire();
                Assert.IsTrue(parser.CurrentState == t.Item2);
            }


            parser.ForceState("Unstable");
            Console.WriteLine("FORCING {{{0}}} ", parser.CurrentState);
            foreach (var t in linesAfter3)
            {
                Console.WriteLine("{{{0}}} / {1} => {{{2}}}", parser.CurrentState, t.Item1, t.Item2);
                sim.AddLine(t.Item1);
                sim.Fire();
                Assert.IsTrue(parser.CurrentState == t.Item2);
            }
            
            // compare callback data

            Assert.AreEqual(linesCallbackActual.Count, linesCallbackExpected.Count);
            for (int i = 0; i < linesCallbackExpected.Count; i++)
            { 
                var a = linesCallbackActual[i];
                var x = linesCallbackExpected[i];
                Assert.AreEqual(x.Item1, a.Item1);
                Assert.AreEqual(x.Item2, a.Item2);
            }
        }

        [TestMethod]
        public void TestFrameworkTest_SimpleParserForConsecutive()
        {
            ParserGrammar grammar = new ParserGrammar();
            grammar.AddTransitionPattern("R1", "Started", "State1", "AAAAA", 1);
            grammar.AddTransitionPattern("R2", "State1", "State2", "BBBBB", 4, consecutive:true);
            grammar.AddTransitionPattern("R2", "State3", "State2", "BBBBB", 4, consecutive: true);

            QuickParser parser = new QuickParser(grammar);

            FileSimulator sim = new FileSimulator();
            parser.BeginAnalysis("Dummy", sim);
            Assert.IsTrue(parser.CurrentState == "Initialized");

            sim.AddLines(
                "Random",
                "Non-matching",
                "lines"
                );
            sim.Fire();
            
            Assert.IsTrue(parser.CurrentState == "Started");
            sim.AddLine("AAAAA");
            sim.Fire();
            Assert.IsTrue(parser.CurrentState == "State1");
            sim.AddLine("AAAAA");
            sim.Fire();
            Assert.IsTrue(parser.CurrentState == "State1");

            for (int i = 0; i < 3; i++)
            {
                sim.AddLine("BBBBB");
                sim.Fire();
                Assert.IsTrue(parser.CurrentState == "State1");
            }
            sim.AddLine("BBBBB");
            sim.Fire();
            Assert.IsTrue(parser.CurrentState == "State2");

            parser.ForceState("State3");
            Assert.IsTrue(parser.CurrentState == "State3");

            for (int i = 0; i < 3; i++)
            {
                sim.AddLine("BBBBB");
                sim.Fire();
                Assert.IsTrue(parser.CurrentState == "State3");
            }
            sim.AddLine("BBBBB");
            sim.Fire();
            Assert.IsTrue(parser.CurrentState == "State2");

            string[] fillers = new string[] {"Random",
                "Non-matching",
                "lines",
                "To fill"};

            parser.ForceState("State3");
            Assert.IsTrue(parser.CurrentState == "State3");
            for (int i = 1; i < 6; i++)
            {
                sim.AddLine("------");
                string[] data = new string[10];
                for (int k = 0; k < 10; k++) data[k] = "BBBBB";
                int cons = 0;
                for (int j = 0; j < 10; j++)
                {
                    if(j % i == 0)
                    {
                        data[j] = "RANDOM";
                        cons = 0;
                    }
                    else
                    {
                        cons++;
                    }
                }
                sim.AddLines(data);
                sim.Fire();
                if(cons>=4)
                {
                    Assert.IsTrue(parser.CurrentState == "State2");
                }
                else
                {
                    Assert.IsTrue(parser.CurrentState == "State3");
                }
            }

        }


    }
    public class FileSimulator : IObservable<List<string>> , IDisposable
    {
        List<string> data = new List<string>();
        IObserver<List<string>> observer;
        public void AddLine(string line)
        {
            data.Add(line);
        }
        public void AddLines(List<string> list)
        {
            data.AddRange(list);
        }
        public void AddLines(params string[] list)
        {
            data.AddRange(list);
        }
        public void Complete()
        {
            observer.OnCompleted();
        }
        public void Fire(bool asBatch = true)
        {
            Fire(data.Count,asBatch);
        }
        public void Fire(int nLines, bool asBatch= true)
        {
            List<string> toSend = new List<string>();
            for (int i = 0; i < nLines; i++)
            {
                toSend.Add(data[0]);
                data.RemoveAt(0);
            }
            if (asBatch)
            {
                observer.OnNext(toSend);
            }
            else
            {
                foreach (string s in toSend)
                {
                    List<string> toSend2 = new List<string>();
                    toSend2.Add(s);
                    observer.OnNext(toSend2);
                }
            }
        }
        public IDisposable Subscribe(IObserver<List<string>> observer)
        {
            this.observer = observer;
            return this;
        }
        public void Dispose()
        {
            
        }
    }
}
