using System;
using System.Net;
using System.Threading.Tasks;
using DotNetty.Transport.Channels;
using Orleans;
using AdventureGrainInterfaces;

namespace AdventureSocketClient
{

    public class MudServerHandler : ChannelHandlerAdapter
    {

        bool waitingForName = true;
        IPlayerGrain _player = null;
        Message _message = null;
        IMessage _messageInterface = null;


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

        public override void ChannelRead(IChannelHandlerContext context, object message)
        {
            try
            {
                string msg = (string)message;
                // Generate and write a response.
                this.ProcessMessage(context, msg);
            }
            catch (Exception ex)
            {

                Console.WriteLine("Exception in ChannelRead0: " + ex);
            }
        }

        public override void ChannelInactive(IChannelHandlerContext context)
        {
            killPlayer();
        }
 
        private async void killPlayer()
        {
            if (_player != null)
            {
                await _player.Play("End"); // disconnect
                await _player.UnSubscribe(_messageInterface);
            }

            await GrainClient.GrainFactory.DeleteObjectReference<IMessage>(_messageInterface);            
            
            _message = null;
            _messageInterface = null;
            _player = null;

        }



        private async void ProcessMessage(IChannelHandlerContext contex, string msg)
        {            

            string response = "";
            bool close = false;
            if (string.IsNullOrEmpty(msg))
            {
                response = "Please type something.\r\n";
            }
            else if (waitingForName)
            {
                waitingForName = false;

                Guid playerGuid = Guid.NewGuid();

                _player = GrainClient.GrainFactory.GetGrain<IPlayerGrain>(playerGuid);
                await _player.SetName(msg);

                _message = new Message(contex);

                //Create a reference for Message usable for subscribing to the observable grain.
                _messageInterface = await GrainClient.GrainFactory.CreateObjectReference<IMessage>(_message);
                await _player.Subscribe(_messageInterface);

                var room1 = GrainClient.GrainFactory.GetGrain<IRoomGrain>(0);
                await _player.SetRoomGrain(room1);

                response = await _player.Play("look");

            }
            else if (string.Equals("End", msg, StringComparison.OrdinalIgnoreCase))
            {
                response = "Have a good day!\r\n";                
                killPlayer();

                close = true;
            }
            else
            {
                response = await _player.Play(msg);
            }

            Task wait_close = contex.WriteAndFlushAsync(response + "\r\n");
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
            _context.WriteAndFlushAsync(message + "\r\n");
        }
    }

}