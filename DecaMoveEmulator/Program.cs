using System;
using System.Threading;
using System.Reflection;
using System.IO;
using HarmonyLib;
using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Net.Sockets;
using System.Net;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;

namespace DecaMoveEmulator
{
	class StatePatch
    {
		public static void MyPostfix(ref object __result)
		{
			__result = Program.status;
		}
	}

	class SerialWritePatch
    {
		public static void MyPrefix(object __instance){}

		public static void MyPostfix(object __instance, string text)
        {
			Console.WriteLine("[Serial Packet] " + text);
			if (Program.udpClient != null)
			{
				var bytes = Encoding.ASCII.GetBytes(text);
				Program.udpClient.Send(bytes, bytes.Length);
			}
        }
    }

	class Program
    {
		public static int status = 0;

		static byte[] batpkt = { 0x62, 0x62, 0x13, 0x37 };//, 0x0D, 0x0A };
		static byte[] verpkt = { 0x76, 0x76, 3, 1, 3, 3, 7, 3, 1, 3, 3, 7, 3, 1, 3, 3, 7, 3, 7, 3, 1, 3, 3, 7, 3, 7 };//, 0x0D, 0x0A };
		static byte[] onpkt = { 0x66, 0x66, 0x01 };//, 0x0D, 0x0A };

		static dynamic decaMoveChannel;
		static MethodInfo processPacket;

		public static UdpClient udpClient;
		static IPEndPoint sender;

		static void Main(string[] args)
        {
			CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
			KillExistingService();
			PatchDMS();
			SendMulticastPacket();
			StreamAndroidData();
        }

		static void KillExistingService()
        {
			var services = Process.GetProcessesByName("DecaMoveService2");
			if(services.Length > 0)
            {
				foreach (var process in services)
					process.Kill();
            }
        }
		
		static void PatchDMS()
        {
			new Thread(() =>
			{
				var currDir = Directory.GetCurrentDirectory();
				Directory.SetCurrentDirectory(@"C:\Program Files\Megadodo Games\DecaHub");

				var dms2asm = Assembly.LoadFrom("DecaMoveService2.exe");
				var dmstype = dms2asm.GetType("DecaMoveService2.DecaMoveService");
				var dmctype = dms2asm.GetType("DecaMoveService2.DecaMoveChannel");
				dynamic decaMoveService = Activator.CreateInstance(dmstype, new object[] { });

				MethodInfo StartService = dmstype.GetMethod("OnStart", BindingFlags.NonPublic | BindingFlags.Instance);
				StartService.Invoke(decaMoveService, new object[] { new string[] { } });

				PropertyInfo DecaMoveChannel = dmstype.GetProperty("DecaMoveChannel", BindingFlags.NonPublic | BindingFlags.Instance);
				decaMoveChannel = DecaMoveChannel.GetValue(decaMoveService);

				var harmony = new Harmony("com.jakecrowley.decapatch");

				MethodInfo getState = dmctype.GetMethod("get_State", BindingFlags.Instance | BindingFlags.Public);
				var mPostfix = typeof(StatePatch).GetMethod("MyPostfix", BindingFlags.Static | BindingFlags.Public);
				harmony.Patch(getState, new HarmonyMethod(mPostfix), new HarmonyMethod(mPostfix));

				MethodInfo write = dmctype.GetMethod("Write", BindingFlags.NonPublic | BindingFlags.Instance);
				var mPrefixWrite = typeof(SerialWritePatch).GetMethod("MyPrefix", BindingFlags.Static | BindingFlags.Public);
				var mPostfixWrite = typeof(SerialWritePatch).GetMethod("MyPostfix", BindingFlags.Static | BindingFlags.Public);
				harmony.Patch(write, new HarmonyMethod(mPrefixWrite), new HarmonyMethod(mPostfixWrite));

				MethodInfo installStuff = dmctype.GetMethod("InstallStuff", BindingFlags.NonPublic | BindingFlags.Instance);
				installStuff.Invoke(decaMoveChannel, new object[] { true });

				processPacket = dmctype.GetMethod("ProcessPacket", BindingFlags.NonPublic | BindingFlags.Instance);
			}).Start();
		}


		static void StreamAndroidData()
        {
			while (decaMoveChannel == null || processPacket == null)
				Thread.Sleep(50);

			new Thread(() =>
			{
				byte[] data = new byte[1024];
				IPEndPoint ipep = new IPEndPoint(IPAddress.Any, 9050);
				udpClient = new UdpClient(ipep);

				while (true)
				{
					processPacket.Invoke(decaMoveChannel, new object[] { verpkt });
					processPacket.Invoke(decaMoveChannel, new object[] { onpkt });

					status = 1;

					Console.WriteLine("[DME] Waiting for a client...");

					while (Encoding.ASCII.GetString(data) != "connected")
					{
						sender = new IPEndPoint(IPAddress.Any, 0);
						data = udpClient.Receive(ref sender);
					}

					status = 2;

					var cnctmsg = Encoding.ASCII.GetBytes("connected");
					udpClient.Send(cnctmsg, cnctmsg.Length, sender);

					Console.WriteLine("[DME] Client connected ");

					string[] msg;

					while (status > 1)
					{
						data = udpClient.Receive(ref sender);
						msg = Encoding.ASCII.GetString(data, 0, data.Length).Split(',');

						ProcessPacket(msg);
					}
				}
			}).Start();

			Console.ReadLine();
		}

		public static void ProcessPacket(string[] msg)
        {
			try
			{
				status = 3;

				if (msg.Length <= 1)
				{
					if (msg[0] == "ping")
					{
						var sendmsg = Encoding.ASCII.GetBytes("pong");
						udpClient.Send(sendmsg, sendmsg.Length, sender);
						return;
					}
					else if (msg[0] == "stop")
					{
						status = 1;
						return;
					}

					ushort battlvl = ushort.Parse(msg[0]);
					byte[] batt = BitConverter.GetBytes(battlvl);
					processPacket.Invoke(decaMoveChannel, new object[] { new byte[] { 0x62, 0x62, batt[0], batt[1] } });
				}
				else
				{
					Quaternion q = new Quaternion(float.Parse(msg[0]), float.Parse(msg[1]), float.Parse(msg[2]), float.Parse(msg[3]));
					processPacket.Invoke(decaMoveChannel, new object[] { EncodeQuaternion(q) });
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("[ERR] Error processing packet: " + string.Join(",", msg.Select(p => p.ToString())));
				Console.WriteLine(e.StackTrace);
			}
		}

		public static void SendMulticastPacket()
        {
			List<Socket> mcastSockets = new List<Socket>();
			IPAddress mcastAddress = IPAddress.Parse("224.0.0.69");
			var endPoint = new IPEndPoint(mcastAddress, 11000);
			var buffer = Encoding.ASCII.GetBytes("DecaMoveAndroidEmulator");

			IPAddress[] localIPs = GetIPAddresses();
			foreach (IPAddress ip in localIPs)
			{
				Socket mcastSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
				IPEndPoint IPlocal = new IPEndPoint(ip, 0);
				mcastSocket.Bind(IPlocal);
				MulticastOption mcastOption = new MulticastOption(mcastAddress, ip);
				mcastSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, mcastOption);
				mcastSockets.Add(mcastSocket);
			}

			var t = new Timer((o) =>
			{
				foreach(Socket mcastSocket in mcastSockets)
					mcastSocket.SendTo(buffer, endPoint);
			});
			t.Change(0, 5000);
		}

		static IPAddress[] GetIPAddresses()
		{
			List<IPAddress> ips = new List<IPAddress>();
			var host = Dns.GetHostEntry(Dns.GetHostName());
			foreach (var ip in host.AddressList)
			{
				if (ip.AddressFamily == AddressFamily.InterNetwork)
				{
					ips.Add(ip);
				}
			}
			return ips.ToArray();
		}

		public static byte[] EncodeQuaternion(Quaternion q)
        {
			byte[] bytes = { 0x78, 0x78, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

			var x = BitConverter.GetBytes((short)(q.X / 6.10351563E-05f));
			bytes[4] = x[0];
			bytes[5] = x[1];

			var y = BitConverter.GetBytes((short)(q.Y / 6.10351563E-05f));
			bytes[6] = y[0];
			bytes[7] = y[1];

			var z = BitConverter.GetBytes((short)(q.Z / 6.10351563E-05f));
			bytes[8] = z[0];
			bytes[9] = z[1];

			var w = BitConverter.GetBytes((short)(q.W / 6.10351563E-05f));
			bytes[2] = w[0];
			bytes[3] = w[1];

			return bytes;
        }
	}
}
