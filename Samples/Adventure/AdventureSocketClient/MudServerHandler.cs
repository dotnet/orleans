using System;
using System.Net;
using System.Threading.Tasks;
using DotNetty.Transport.Channels;
using Orleans;
using AdventureGrainInterfaces;

namespace AdventureSocketClient
{

    public class MudServerHandler : SimpleChannelInboundHandler<string>
    {

        bool waitingForName = true;
        IPlayerGrain player = null;

        public override void ChannelActive(IChannelHandlerContext contex)
        {
            contex.WriteAsync(@"
  ___      _                 _                  
 / _ \    | |               | |                 
/ /_\ \ __| |_   _____ _ __ | |_ _   _ _ __ ___ 
|  _  |/ _` \ \ / / _ \ '_ \| __| | | | '__/ _ \
| | | | (_| |\ V /  __/ | | | |_| |_| | | |  __/
\_| |_/\__,_| \_/ \___|_| |_|\__|\__,_|_|  \___|");

            contex.WriteAsync(string.Format("\r\nWelcome to {0} !\r\n", Dns.GetHostName()));
            contex.WriteAsync(string.Format("It is {0} now !\r\n\r\n", DateTime.Now));

            contex.WriteAndFlushAsync("What's you name?");

        }

        protected override void ChannelRead0(IChannelHandlerContext contex, string msg)
        {
            // Generate and write a response.

            ProcessRequest(contex, msg).Wait();
        }

        private async Task ProcessRequest(IChannelHandlerContext contex, string msg)
        {
            string response = "";
            bool close = false;
            if (string.IsNullOrEmpty(msg))
            {
                response = "Please type something.\r\n";
            }

            if (waitingForName)
            {
                waitingForName = false;

                Guid playerGuid = Guid.NewGuid();

                player = GrainClient.GrainFactory.GetGrain<IPlayerGrain>(playerGuid);
                await player.SetName(msg);

                Message m = new Message(contex);

                //Create a reference for Message usable for subscribing to the observable grain.
                var obj = await GrainClient.GrainFactory.CreateObjectReference<IMessage>(m);
                await player.Subscribe(obj);

                var room1 = GrainClient.GrainFactory.GetGrain<IRoomGrain>(0);
                await player.SetRoomGrain(room1);

                response = player.Play("look").Result;

            }
            else if (string.Equals("bye", msg, StringComparison.OrdinalIgnoreCase))
            {
                response = "Have a good day!\r\n";
                await player.Play("End"); // disconnect

                //player.UnSubscribe()
                player = null;

                close = true;
            }
            else
            {
                response = player.Play(msg).Result;
            }

            Task wait_close = contex.WriteAndFlushAsync(response);
            if (close)
            {
                Task.WaitAll(wait_close);
                contex.CloseAsync();
            }

        }

        public override void ChannelReadComplete(IChannelHandlerContext contex)
        {
            contex.Flush();
        }

        public override void ExceptionCaught(IChannelHandlerContext contex, Exception e)
        {
            Console.WriteLine("{0}", e.StackTrace);
            contex.CloseAsync();
        }

        public override bool IsSharable => true;
    }

    public class Message : IMessage
    {
        IChannelHandlerContext _context;
        public Message(IChannelHandlerContext context)
        {
            _context = context;
        }
        public void ReceiveMessage(string message)
        {
            _context.WriteAndFlushAsync(message);
        }
    }

}