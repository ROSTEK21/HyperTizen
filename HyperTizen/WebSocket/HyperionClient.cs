using Newtonsoft.Json;
using System.Threading.Tasks;
using Tizen.Applications;
using HyperTizen.WebSocket.DataTypes;
using System.Net.WebSockets;
using System.Text;
using System;
using System.Threading;
using System.Diagnostics;

namespace HyperTizen.WebSocket
{
    internal class HyperionClient
    {
        WebSocketClient client;
        private int _isRunning;
        public HyperionClient()
        {
            client = new WebSocketClient(App.Configuration.RPCServer);
            Task.Run(() => client.ConnectAsync());
            Task.Run(() => Start());
        }

        public void UpdateURI()
        {
            if (client?.client != null && client.client.State == WebSocketState.Open)
            {
                client.client.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
            }

            client = new WebSocketClient(App.Configuration.RPCServer);
            Task.Run(() => client.ConnectAsync());
        }

        public async Task Start(bool shouldStart = false)
        {
            if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
            {
                if (shouldStart)
                    App.Configuration.Enabled = true;
                return;
            }

            try
            {
                if (client != null && Capturer.GetCondition())
                {
                    string previousImage = null;
                    byte[] buffer = null;
                    while (App.Configuration.Enabled || shouldStart)
                    {
                        long frameStart = Stopwatch.GetTimestamp();
                        Color[] colors = Capturer.GetColors();
                        string image = Capturer.ToImage(colors);

                        if (!ReferenceEquals(image, previousImage))
                        {
                            ImageCommand imgCmd = new ImageCommand(image);
                            string message = JsonConvert.SerializeObject(imgCmd);
                            buffer = Encoding.UTF8.GetBytes(message);
                            previousImage = image;
                        }

                        if (client?.client?.State == WebSocketState.Open)
                        {
                            await client.client.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
                        }

                        long frameTicks = Stopwatch.GetTimestamp() - frameStart;
                        long minimumFrameTicks = Stopwatch.Frequency / 60;
                        if (frameTicks < minimumFrameTicks)
                        {
                            int delayMs = (int)Math.Ceiling((minimumFrameTicks - frameTicks) * 1000.0 / Stopwatch.Frequency);
                            if (delayMs > 0)
                                await Task.Delay(delayMs);
                        }

                        if (App.Configuration.Enabled && shouldStart) shouldStart = false;
                        else if (!App.Configuration.Enabled && shouldStart) App.Configuration.Enabled = true;
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _isRunning, 0);
            }
        }

        public async Task Stop()
        {
            if (App.Configuration.Enabled) App.Configuration.Enabled = false;
            Color[] colors = Capturer.GetColors();
            string image = Capturer.ToImage(colors);

            if (client?.client?.State == WebSocketState.Open)
            {
                ImageCommand imgCmd = new ImageCommand(image);
                string message = JsonConvert.SerializeObject(imgCmd);
                var buffer = Encoding.UTF8.GetBytes(message);
                await client.client.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
    }
}