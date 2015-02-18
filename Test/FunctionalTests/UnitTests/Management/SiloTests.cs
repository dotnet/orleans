using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Runtime;

using System.Threading;
using System.Net;

namespace UnitTests
{
    //[TestClass]
    public class SiloTests
    {
        ////[TestMethod]
        //public void Silo_TwoSilosCommunicating()
        //{
        //    //ServerConfigManager scm1 = ServerConfigManager.LoadConfigManager();
        //    //scm1.NetworkConfig.MessagingEndpoint = new IPEndPoint(IPAddress.Loopback, 31444);
        //    //scm1.NetworkConfig.RingEndpoint = new IPEndPoint(IPAddress.Loopback, 31555);
        //    //scm1.NetworkConfig.SeedNodeList = new List<IPEndPoint>();
        //    //scm1.NetworkConfig.SeedNodeList.Add(scm1.NetworkConfig.RingEndpoint);
        //    ConfigBag configBag1 = new ConfigBag
        //    {
        //        MessagingEndpoint = new IPEndPoint(IPAddress.Loopback, 31444),
        //        RingEndpoint = new IPEndPoint(IPAddress.Loopback, 31555)
        //    };
        //    configBag1.SeedNodes = new List<IPEndPoint>();
        //    configBag1.SeedNodes.Add(configBag1.RingEndpoint);

        //    //ServerConfigManager scm2 = ServerConfigManager.LoadConfigManager();
        //    //scm2.NetworkConfig.MessagingEndpoint = new IPEndPoint(IPAddress.Loopback, 32444);
        //    //scm2.NetworkConfig.RingEndpoint = new IPEndPoint(IPAddress.Loopback, 32555);
        //    //scm2.NetworkConfig.SeedNodeList = new List<IPEndPoint>();
        //    //scm2.NetworkConfig.SeedNodeList.Add(scm1.NetworkConfig.RingEndpoint);
        //    ConfigBag configBag2 = new ConfigBag
        //    {
        //        MessagingEndpoint = new IPEndPoint(IPAddress.Loopback, 32444),
        //        RingEndpoint = new IPEndPoint(IPAddress.Loopback, 32555)
        //    };
        //    configBag1.SeedNodes = new List<IPEndPoint>();
        //    configBag1.SeedNodes.Add(configBag1.RingEndpoint);

        //    Silo silo1 = new Silo(Silo.SiloType.Primary, configBag1);

        //    Silo silo2 = new Silo(Silo.SiloType.Secondary, configBag2);

        //    silo1.Start();

        //    silo2.Start();

        //    // Wait to let CAS percolate
        //    Thread.Sleep(7500);

        //    AutoResetEvent done = new AutoResetEvent(false);

        //    TestMessageTarget t1 = new TestMessageTarget(silo1, done);

        //    TestMessageTarget t2 = new TestMessageTarget(silo2, done);

        //    // Wait to let CAS percolate
        //    Thread.Sleep(15000);

        //    // Now let's dump the state of each silo's directory
        //    Console.WriteLine("Silo 1 state:");
        //    Console.WriteLine(silo1.ToString());
        //    Console.WriteLine();
        //    Console.WriteLine("Silo 2 state:");
        //    Console.WriteLine(silo2.ToString());

        //    // Wait to let CAS percolate
        //    Thread.Sleep(7500);

        //    t1.SendRequest(t2.Grain, t2.Activation);

        //    bool received = done.WaitOne(30000);

        //    silo1.Stop();
        //    silo2.Stop();

        //    Assert.IsTrue(received, "Response not received in 30 seconds");
        //}

        //private class TestMessageTarget : IMessageTarget
        //{
        //    private AutoResetEvent done;
        //    private Silo silo;
        //    private CorrelationId cid;

        //    public TestMessageTarget(Silo s, AutoResetEvent e)
        //    {
        //        Grain = new GrainID();
        //        Activation = new ActivationID();
        //        silo = s;
        //        done = e;
        //        silo.RegisterMessageTarget(this);
        //    }

        //    public void SendRequest(GrainID g, ActivationID a)
        //    {
        //        Message request = new Message(Message.Categories.Application, Message.Directions.Request);
        //        request.SendingGrain = Grain;
        //        request.SendingActivation = Activation;
        //        request.TargetGrain = g;
        //        request.TargetActivation = a;
        //        request.BodyString = "Request";
        //        cid = CorrelationId.GetNext();
        //        request.Id = cid;
        //        silo.SendMessageInternal(request);
        //    }

        //    #region IMessageTarget Members

        //    public GrainID Grain
        //    {
        //        get;
        //        set;
        //    }

        //    public ActivationID Activation
        //    {
        //        get;
        //        set;
        //    }

        //    public void HandleNewRequest(Message request)
        //    {
        //        Assert.AreEqual<string>("Request", request.BodyString, "Request body is incorrect");
        //        Message response = request.CreateResponseMessage();
        //        response.BodyString = "Response";
        //        silo.SendMessageInternal(response);
        //    }

        //    public void HandleResponse(Message response)
        //    {
        //        Assert.AreEqual<string>("Response", response.BodyString, "Response body is incorrect");
        //        Assert.AreEqual<CorrelationId>(cid, response.Id, "Correlation IDs don't match");
        //        done.Set();
        //    }

        //    #endregion
        //}
    }
}
