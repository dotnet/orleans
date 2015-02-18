using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.Threading;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Messaging;
using Orleans.RuntimeCore;
//using Orleans.Communicators;

namespace UnitTests
{
    [TestClass]
    public class MessageCenterBasicTests
    {
        private enum TestMode
        {
            Sender, Receiver, Silent, IComm
        }

        [TestInitialize]
        [TestCleanup]
        public void MyTestCleanup()
        {
            OrleansTask.Reset();
        }

        public void MainRunner(string[] args)
        {
            bool noisy;
            TestMode mode = GetOptions(args, out noisy);

            if (noisy)
            {
                Console.Out.WriteLine("Starting!");
            }

            switch (mode)
            {
                case TestMode.Receiver:
                    Receiver();
                    break;
                case TestMode.Sender:
                    Sender();
                    break;
                case TestMode.Silent:
                    MessageCenterTest_Silent();
                    break;
                //case TestMode.IComm:
                //    MessageCenterTest_IComm();
                //    break;
            }

            if (noisy)
            {
                Console.Out.WriteLine("Hit return to exit");
                Console.In.ReadLine();
            }
        }

        private void Receiver()
        {
            MessageCenter mc = new MessageCenter(new IPEndPoint(Utils.GetLocalIPAddress(), 0));
            mc.Router = new TrivialRouter(mc);
            mc.Start();
            Console.Out.WriteLine("Started receiving");
            Console.In.ReadLine();
            CancellationToken ct = new CancellationToken();
            Message msg = mc.WaitMessage(Message.Categories.Application, ct);
            Console.Out.WriteLine("Received message:");
            Console.Out.WriteLine(msg.ToString());
            mc.Stop();
        }

        private void Sender()
        {
            MessageCenter mc = new MessageCenter(new IPEndPoint(Utils.GetLocalIPAddress(), 0));
            mc.Router = new TrivialRouter(mc);
            mc.Start();
            Console.Out.WriteLine("Type a message to send");
            string msg = Console.In.ReadLine();
            GrainId target = GrainId.GetGrainId("target");
            GrainId sender = GrainId.GetGrainId("sender");
            ActivationId sendingActivation = new ActivationId();
            CorrelationId cid = SendRequest(mc, target, sender, sendingActivation, msg);
            Console.Out.WriteLine("Queued message for sending with correlation ID " + cid.ToString());
            mc.Stop();
        }

        private CorrelationId SendRequest(IMessageCenter mc, GrainId target, GrainId sender, ActivationId sendingActivation, string text)
        {
            CorrelationId cid = CorrelationId.GetNext();
            Message request = new Message(Message.Categories.Application, Message.Directions.Request);
            request.Id = cid;
            request.TargetGrain = target;
            request.SendingGrain = sender;
            request.SendingActivation = sendingActivation;
            //request.SetStringBody(text);
            request.BodyObject = text;
            mc.SendMessage(request);
            return cid;
        }

        [TestMethod]
        public void MessageCenterTest_Silent()
        {
            // Get everything set up
            MessageCenter sender = new MessageCenter(new IPEndPoint(Utils.GetLocalIPAddress(), 0));
            sender.Router = new TrivialRouter(sender);
            MessageCenter receiver = new MessageCenter(new IPEndPoint(Utils.GetLocalIPAddress(), 0));
            receiver.Router = new TrivialRouter(receiver);
            ((TrivialRouter)sender.Router).PartnerMC = receiver;
            ((TrivialRouter)receiver.Router).PartnerMC = sender;
            sender.Start();
            receiver.Start();

            // Create our IDs
            GrainId targetGrain = new GrainId();
            ActivationId targetActivation = new ActivationId();
            GrainId sendingGrain = new GrainId();
            ActivationId sendingActivation = new ActivationId();

            // Send the request
            string message = "This is a test";
            CorrelationId sentId = SendRequest(sender, targetGrain, sendingGrain, sendingActivation, message);

            // Get the request
            CancellationToken ct = new CancellationToken();
            Message received = receiver.WaitMessage(Message.Categories.Application, ct);
            Assert.AreEqual<CorrelationId>(sentId, received.Id, "Correlation IDs don't agree between sender and receiver");
            Assert.AreEqual<Message.Directions>(Message.Directions.Request, received.Direction, 
                "Request has the wrong direction");
            Message request = received as Message;
            AssertEquals<string>(message, (string)request.BodyObject, "Request message bodies don't agree");

            // Send a reply
            string reply = "This is a reply";
            Message response = received.CreateResponseMessage();
            response.Result = Message.ResponseTypes.Success;
            response.BodyObject = reply;
            receiver.SendMessage(response);

            // Get the reply
            Message resp = sender.WaitMessage(Message.Categories.Application, ct);
            Assert.AreEqual<CorrelationId>(sentId, resp.Id, "Correlation IDs don't agree between request and response");
            Assert.AreEqual<Message.Directions>(Message.Directions.Response, resp.Direction, 
                "Request has the wrong direction");
            AssertEquals<string>(reply, (string)resp.BodyObject, "Response message bodies don't agree");

            // Clean up
            receiver.Stop();
            sender.Stop();

            return;
        }


        //// Since the ICommunicator interface includes a bunch of delegates, we need to make these static so that the 
        //// callbacks can see them
        //private MessageCenterCommunicator receiver;
        //private GrainID targetGrain;
        //private ActivationID targetActivation;
        //private byte[] msg = { 0x01, 0x02, 0x03, 0x04 };
        //private byte[] reply = { 0x04, 0x03, 0x02, 0x01 };
        //private AutoResetEvent doneFlag;

        //[TestMethod]
        //public void MessageCenterTest_IComm()
        //{
        //    // Set everything up
        //    IPAddress myIp = Utils.GetLocalIPAddress();
        //    //IPAddress ip = IPAddress.Any;
        //    //IPAddress ip = IPAddress.Loopback;
        //    MessageCenter mc1 = new MessageCenter(new IPEndPoint(myIp, 0));
        //    mc1.Router = new TestRouter(mc1);
        //    MessageCenter mc2 = new MessageCenter(new IPEndPoint(myIp, 0));
        //    mc2.Router = new TestRouter(mc2);
        //    MessageCenterCommunicator sender = new MessageCenterCommunicator(mc1);
        //    receiver = new MessageCenterCommunicator(mc2);
        //    sender.Name = "sender";
        //    receiver.Name = "receiver";
        //    sender.Start();
        //    receiver.Start();

        //    // Create our IDs
        //    targetGrain = new GrainID();
        //    targetActivation = new ActivationID();
        //    GrainID sendingGrain = new GrainID();
        //    ActivationID sendingActivation = new ActivationID();

        //    // Set up routing
        //    sender.AddRouting(targetGrain, receiver.GetAddress());
        //    //receiver.AddRouting(sendingGrain, sender.GetAddress());

        //    // Set up the request-arrived callback
        //    receiver.RegisterRequestListener(targetGrain, RequestArrivedCallback);

        //    // Set up an event to wait on
        //    doneFlag = new AutoResetEvent(false);

        //    // Send the request
        //    sender.SendRemoteMessage(targetGrain, sendingGrain, sendingActivation, msg, doneFlag, (Action<Message, object>)ResponseReceivedCallback);

        //    // Wait for everything to be done
        //    Assert.IsTrue(doneFlag.WaitOne(5000), "No response received within 5 seconds");

        //    // And clean up
        //    sender.Stop();
        //    receiver.Stop();
        //}

        //void RequestArrivedCallback(Message request)
        //{
        //    AssertArrayEquals<byte>(request.Body, msg, "Request message bodies don't agree");
        //    receiver.SendResponse(targetGrain, targetActivation, request, reply);
        //}

        //void ResponseReceivedCallback(Message response, Object context)
        //{
        //    AssertArrayEquals<byte>(response.Body, reply, "Response message bodies don't agree");
        //    AssertEquals<Object>(context, doneFlag, "Context object is not correct");
        //    doneFlag.Set();
        //}

        static void AssertEquals<T>(T val1, T val2, string error)
        {
            if (!val1.Equals(val2))
            {
                throw new ApplicationException(error + ": value 1 is '" + val1.ToString() + "', value 2 is '" + val2.ToString() + "'");
            }
        }

        static void AssertArrayEquals<T>(T[] val1, T[] val2, string error)
        {
            if (val1.Length == val2.Length)
            {
                for (int n = 0; n < val1.Length; n++)
                {
                    if (!val1[n].Equals(val2[n]))
                    {
                        throw new ApplicationException(error + ": value 1 is '" + val1.ToString() + "', value 2 is '" + val2.ToString() + "'");
                    }
                }
            }
            else
            {
                throw new ApplicationException(error + ": value 1 is '" + val1.ToString() + "', value 2 is '" + val2.ToString() + "'");
            }
        }

        private TestMode GetOptions(string[] args, out bool noisy)
        {
            TestMode mode = TestMode.Silent;

            if (args.Length > 0)
            {
                if (!Enum.TryParse<TestMode>(args[0], true, out mode))
                {
                    Console.Out.WriteLine("Unknown mode requested: '" + args[0] + "'");
                    Console.Out.WriteLine("Supported modes are: ");
                    foreach (string name in Enum.GetNames(typeof(TestMode)))
                    {
                        Console.Out.WriteLine("    " + name);
                    }
                    throw new NotImplementedException("Requested mode '" + args[0] + "' is not supported.");
                }
            }

            switch (mode)
            {
                case TestMode.Receiver:
                case TestMode.Sender:
                    noisy = true;
                    break;
                default:
                    //noisy = false;
                    noisy = true;
                    break;
            }
            return mode;
        }
    }
}
