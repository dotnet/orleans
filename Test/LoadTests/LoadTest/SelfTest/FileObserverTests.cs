using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.TestFramework;
using System.Threading;
using System.IO;
using System.Threading.Tasks;

namespace Orleans.TestFramework.SelfTest
{
    [TestClass]
    public class FileObserverTests
    {
        [TestMethod]
        public void TestFrameworkTest_SimpleObservation()
        {
            Task[] tasks = new Task[] {
                Task.Factory.StartNew(()=> {WriteFile("TestSimpleObservation.txt",100);}),
                Task.Factory.StartNew(()=> {ReadFile("TestSimpleObservation.txt",100);})
            };
            Task.WaitAll(tasks);
            File.Delete("TestSimpleObservation.txt");
        }

        public void WriteFile(string fileName, int max=1000)
        {
            Random rnd = new Random();
            
            int batchNumber = 0;
            bool brakeLine = false;
            while (totalWritten < max)
            {
                try
                {
                    using (StreamWriter writer = new StreamWriter(fileName, append: true))
                    {
                        int batchSize = 1 + rnd.Next(100); // write random number of lines
                        brakeLine = true;//(rnd.Next(10) > 5);
                        for (int i = 0; i < batchSize; i++)
                        {
                            string s = string.Format("START totalWritten:{0},currentBatchRead:{1},batchsize:{2},item:{3},ticks:{4}, FINISH", totalWritten++, batchNumber, batchSize, i, DateTime.Now.Ticks);

                            if (brakeLine)
                            {
                                int split = rnd.Next(s.Length-2);
                                string s1 = s.Substring(0, split);
                                string s2 = s.Substring(split, s.Length - split);
                                writer.Write(s1);
                                writer.Flush();
                                Thread.Sleep(rnd.Next(100));
                                writer.WriteLine(s2);
                            }
                            else
                            {
                                writer.WriteLine(s);
                                writer.Flush();
                            }
                        }
                        batchNumber++;
                    }
                }
                catch (Exception )
                {
                }
                Thread.Sleep(rnd.Next(100));
            }
        }
        int totalWritten = 0;
        int totalRead =0;
        int currentBatchRead =0;
        int batchSizeRead =0;
        int i=0;
        public void ReadFile(string fileName, int max=1000)
        {
            FileObserver observer = new FileObserver(fileName,10,100,10000);
            
            observer.Subscribe(batchOfLines=>{
                foreach (string line in batchOfLines)
                {
                    // Assert that you are getting an entire line
                    Assert.IsTrue(line.StartsWith("START"));
                    Assert.IsTrue(line.EndsWith("FINISH"));

                    // extract info
                    string[] parts = line.Split(',');
                    int lineNumber = int.Parse(parts[0].Split(':')[1]);
                    int rcvdBatch = int.Parse(parts[1].Split(':')[1]);
                    int rcvdBatchSize = int.Parse(parts[2].Split(':')[1]);
                    int itemNumber = int.Parse(parts[3].Split(':')[1]);
                    
                    // start checking that lines are recieved in order.
                    Assert.AreEqual(totalRead, lineNumber);
                    totalRead++;
                    
                    if (currentBatchRead != rcvdBatch)
                    {
                        Assert.AreEqual(currentBatchRead + 1, rcvdBatch, "Missing lines");
                        currentBatchRead++;
                        batchSizeRead = rcvdBatchSize;
                        i = 0;
                    }
                    Assert.IsTrue(itemNumber >= 0 );
                    Assert.IsTrue(itemNumber < rcvdBatchSize);
                    Assert.AreEqual(i, itemNumber);
                    i++;
                }
            });

            while (!observer.Done) ;
            Assert.AreEqual(totalWritten,totalRead,"Missing lines");
        }
    }
}
