using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

public class VideoFromFrontyLocally : MonoBehaviour 
{
  public delegate void FrameCallback(byte[] jpg, long frameNum, long timestamp);

  public string defaultFrontyUrl = "http://localhost:8003";
  public bool autoConnect;

  public string LastError => lastError;
  public bool IsConnected => connected;

  bool connected;
  string lastError;
  bool inited;
  bool quitting;
  public event FrameCallback OnNewFrame;

  internal static IPAddress HostNameToAddr(Uri host)
  {
    var addrs = Dns.GetHostAddresses(host.Host);
    int i = 0;
    while (true)
    {
      i++;
      try
      {
        var ipv4 = addrs.FirstOrDefault(addr => addr.AddressFamily == AddressFamily.InterNetwork);
        return ipv4 ?? addrs[0];
      }
      catch (SocketException)
      {
        if (i > 3) throw;
        Thread.Sleep(400);
      }
    }
  }
  private void Start()
  {
    if (autoConnect)
      ConnectToFronty();
  }

  public void ConnectToFronty(string videoUrl = null)
  {
    if (inited) return;
    inited = true;
    videoUrl ??= defaultFrontyUrl;
    Debug.Log("Trying to connect to " + videoUrl);
    try
    {
      var host = new Uri(videoUrl);
      var addr = HostNameToAddr(host);
      new Thread(KeepConnectionAlive).Start(new IPEndPoint(addr, host.Port));
    }
    catch (Exception e)
    {
      inited = false;
      connected = false;
      lastError = e.Message;
      UnityEngine.Debug.LogError("Failed to connect to fronty: " + lastError);
    }
  }

  public void OnDestroy()
  {
    quitting = true;
  }

  void KeepConnectionAlive(object endpoint)
  {
    while (true)
    {
      lastError = null;
      connected = false;
      var t = new Thread(MainThread);
      t.Start(endpoint);
      t.Join();
      if (quitting) break;
      Thread.Sleep(1000);
    }
  }

  void MainThread(object _endpoint)
  {
    var ipendpoint = _endpoint as IPEndPoint;
    try
    {
      using (var tcp = new TcpClient(ipendpoint.Address.AddressFamily))
      {
        tcp.Connect(ipendpoint);
        connected = tcp.Connected;
        if (!connected)
          return;
        Debug.Log("Connected to fronty");

        using (var stream = tcp.GetStream())
        using (var reader = new BinaryReader(stream))
          while (tcp.Connected && !quitting)
          {
            var frameNum = reader.ReadInt64();
            var frameLen = reader.ReadInt64();
            var timestamp = reader.ReadInt64();

            if (frameNum < 0 || frameLen <= 0)
            {
              UnityEngine.Debug.LogWarning("Video client got invalid data");
              lastError = "Video client got invalid data";
              break;
            }

            var data = reader.ReadBytes((int)frameLen);

            try
            {
              OnNewFrame?.Invoke(data, frameNum, timestamp);
            }
            catch (Exception e)
            {
              UnityEngine.Debug.LogWarning("OnNewFrame callback threw exception: " + e.Message);
            }
          }
      }
    }
    catch (Exception ex)
    {
      lastError = ex.Message;
      connected = false;
      UnityEngine.Debug.LogWarning(lastError);
    }
  }
}
