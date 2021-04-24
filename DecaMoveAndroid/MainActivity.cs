using System;
using Android.App;
using Android.OS;
using Android.Runtime;
using Android.Views;
using AndroidX.AppCompat.Widget;
using AndroidX.AppCompat.App;
using Google.Android.Material.FloatingActionButton;
using Google.Android.Material.Snackbar;
using Xamarin.Essentials;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading;
using Android.Widget;
using System.Numerics;

namespace DecaMoveAndroid
{
    [Activity(Label = "@string/app_name", Theme = "@style/AppTheme.NoActionBar", MainLauncher = true)]
    public class MainActivity : AppCompatActivity
    {
        private UdpClient udpClient;
        private Thread udpThread;

        private DateTime lastPing = DateTime.Now;

        private bool Running = false;

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            Xamarin.Essentials.Platform.Init(this, savedInstanceState);
            SetContentView(Resource.Layout.activity_main);

            AndroidX.AppCompat.Widget.Toolbar toolbar = FindViewById<AndroidX.AppCompat.Widget.Toolbar>(Resource.Id.toolbar);
            SetSupportActionBar(toolbar);

            AppCompatButton fab = FindViewById<AppCompatButton>(Resource.Id.button1);
            fab.Click += FabOnClick;

            OrientationSensor.ReadingChanged += OrientationSensor_ReadingChanged;
            Battery.BatteryInfoChanged += Battery_BatteryInfoChanged;

            MulticastListener();
        }

        public override bool OnCreateOptionsMenu(IMenu menu)
        {
            MenuInflater.Inflate(Resource.Menu.menu_main, menu);
            return true;
        }

        public override bool OnOptionsItemSelected(IMenuItem item)
        {
            int id = item.ItemId;
            if (id == Resource.Id.action_settings)
            {
                return true;
            }

            return base.OnOptionsItemSelected(item);
        }

        private void MulticastListener()
        {
            UdpClient mcastClient = new UdpClient();
            IPAddress multicastaddress = IPAddress.Parse("224.0.0.69");
            IPEndPoint remoteep = new IPEndPoint(IPAddress.Any, 11000);
            mcastClient.Client.Bind(remoteep);
            mcastClient.JoinMulticastGroup(multicastaddress);

            new Thread(() =>
            {
                while (true)
                {
                    byte[] resp = mcastClient.Receive(ref remoteep);
                    var str = Encoding.ASCII.GetString(resp);
                    if(str == "DecaMoveAndroidEmulator")
                    {
                        AppCompatEditText ipaddr = FindViewById<AppCompatEditText>(Resource.Id.textInputEditText1);
                        RunOnUiThread(() =>
                        {
                            ipaddr.Text = remoteep.Address.ToString();
                        });
                        break;
                    }
                }
            }).Start();
        }

        static IPAddress GetLocalIPAddress()
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    return ip;
                }
            }
            return null;
        }

        private void FabOnClick(object sender, EventArgs eventArgs)
        {
            AppCompatButton view = (AppCompatButton)sender;
            try
            {
                TextView status = FindViewById<TextView>(Resource.Id.status);
                AppCompatEditText ipaddr = FindViewById<AppCompatEditText>(Resource.Id.textInputEditText1);

                if (!OrientationSensor.IsMonitoring)
                {
                    IPEndPoint ipep = new IPEndPoint(IPAddress.Parse(ipaddr.Text), 9050);
                    udpClient = new UdpClient(9050);
                    udpClient.Connect(ipep);

                    var msg = Encoding.ASCII.GetBytes($"connected");
                    udpClient.Send(msg, msg.Length);

                    IPEndPoint udpsender = new IPEndPoint(IPAddress.Any, 0);

                    status.Text = "Status: Connecting...";

                    udpThread = new Thread(() =>
                    {
                        while (true)
                        {
                            var data = udpClient.Receive(ref udpsender);
                            var msg = Encoding.ASCII.GetString(data, 0, data.Length);

                            if (msg == "connected" || msg == "pong")
                            {
                                if (msg == "connected")
                                {
                                    RunOnUiThread(() =>
                                    {
                                        status.Text = "Status: Connected";
                                    });

                                    var battmsg = Encoding.ASCII.GetBytes($"{Battery.ChargeLevel}");
                                    udpClient.Send(battmsg, battmsg.Length);
                                }

                                Running = true;
                                lastPing = DateTime.Now;
                            }
                        }
                    });
                    udpThread.Start();

                    lastPing = DateTime.Now;

                    Timer connCheckTimer = new Timer(connectionCheck);
                    connCheckTimer.Change(0, 5000);

                    OrientationSensor.Start(SensorSpeed.Fastest);
                    view.Text = "Stop";
                }
                else
                {
                    Running = false;
                    OrientationSensor.Stop();
                    view.Text = "Start";
                    status.Text = "Status: Not Running";

                    var msg = Encoding.ASCII.GetBytes("stop");
                    udpClient.Send(msg, msg.Length);
                }
            } 
            catch(Exception e)
            {
                Snackbar.Make(view, e.Message, Snackbar.LengthLong).SetAction("Action", (View.IOnClickListener)null).Show();
            }
        }

        private void connectionCheck(object o)
        {
            if (udpClient != null)
            {
                var msg = Encoding.ASCII.GetBytes("ping");
                udpClient.Send(msg, msg.Length);

                if (Running)
                {
                    if ((DateTime.Now - lastPing).TotalSeconds > 10)
                    {
                        RunOnUiThread(() =>
                        {
                            TextView status = FindViewById<TextView>(Resource.Id.status);
                            status.Text = "Status: Lost Connection";
                        });

                        for (int i = 0; i < 10; i++)
                        {
                            Vibration.Vibrate();
                            Thread.Sleep(100);
                        }
                    }
                }
            }
        }

        private void OrientationSensor_ReadingChanged(object sender, OrientationSensorChangedEventArgs e)
        {
            var o = e.Reading.Orientation;

            var msg = Encoding.ASCII.GetBytes($"{o.X},{o.Y},{o.Z},{o.W}");
            udpClient.Send(msg, msg.Length);
        }

        private void Battery_BatteryInfoChanged(object sender, BatteryInfoChangedEventArgs e)
        {
            if (udpClient != null)
            {
                var msg = Encoding.ASCII.GetBytes($"{e.ChargeLevel}");
                udpClient.Send(msg, msg.Length);
            }
        }

        public override void OnRequestPermissionsResult(int requestCode, string[] permissions, [GeneratedEnum] Android.Content.PM.Permission[] grantResults)
        {
            Xamarin.Essentials.Platform.OnRequestPermissionsResult(requestCode, permissions, grantResults);

            base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        }
	}
}
