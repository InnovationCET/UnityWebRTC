using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Unity.WebRTC;
using UnityEngine;

class SignalChannel
{
  private static HttpClient http;
  public static bool Inited { get; private set; }

  public string local_mailbox { get; private set; }
  public string remote_mailbox { get; private set; }
  private readonly CancellationToken token;
  private readonly ConcurrentQueue<Request> incoming;
  private bool alive;

  public static void InitIfNeeded(string server_base_address, int timeout_ms = 3000)
  {
    if (!Inited)
    {
      http = new HttpClient { BaseAddress = new Uri(server_base_address), Timeout = TimeSpan.FromMilliseconds(timeout_ms) };
      Inited = true;
    }
  }

  public bool TryGet(out Request req) => incoming.TryDequeue(out req);

  // Listen for an incoming connection and accept.
  // Listens to only 1 call !
  // returns: local_channel_name, remote_channel_name (those can be used to build a new SignalChannel)
  // on failure: returns (null,null)
  public static async Task<(string, string)> Listen(string local, CancellationToken token = default)
  {
    while (!token.IsCancellationRequested)
    {
      try
      {
        var msg = await get(local, token);
        if (msg.type == "connect")
        {
          Debug.Log("received the connect message from " + msg.from);
          var local_channel = local + "_" + Guid.NewGuid().ToString();
          var reply = await http.PostAsync(msg.from, as_json(new Request { type = "ok", switch_channel = local_channel }), token);
          reply.EnsureSuccessStatusCode();
          return (local_channel, msg.from);
        }
      }
      catch (HttpRequestException)
      {
        await Task.Delay(1000);
      }
    }
    return (null, null);
  }

  // Calls a remote RTC peer to establish a signalling chanel.
  // Throws exception on failure
  public static async Task<SignalChannel> Call(string remote, string local = "", CancellationToken token = default)
  {
    var local_channel = local + "_" + Guid.NewGuid().ToString();
    var request = await http.PostAsync(remote, as_json(new Request { type = "connect", from = local_channel }), token);
    request.EnsureSuccessStatusCode();

    var timer = new CancellationTokenSource();
    timer.CancelAfter(3000);
    var linked_token = CancellationTokenSource.CreateLinkedTokenSource(timer.Token, token);
    var reply = await get(local_channel, linked_token.Token);

    if (reply != null && reply.type == "ok")
    {
      return new SignalChannel(local_channel, reply.switch_channel ?? reply.from, token);
    }
    throw new Exception("Remote refused to answer the call");
  }

  public SignalChannel(string local, string remote, CancellationToken token = default)
  {
    this.local_mailbox = local;
    this.remote_mailbox = remote;
    this.token = token;
    this.incoming = new ConcurrentQueue<Request>();
    this.alive = true;
    Task.Run(poll);
  }

  internal void Close()
  {
    throw new NotImplementedException();
  }

  private void poll()
  {
    while (!token.IsCancellationRequested && alive)
    {
      incoming.Enqueue(get(local_mailbox, token, () => alive).Result);
    }
  }

  public static implicit operator bool(SignalChannel c) => c != null;

  private static HttpContent as_json(object o) => new ByteArrayContent(Encoding.ASCII.GetBytes(JsonUtility.ToJson(o)));

  public Task<HttpResponseMessage> send(RTCSessionDescription desc) => send(new Request(desc));

  public Task<HttpResponseMessage> SendIceCandidate(RTCIceCandidate candidate) => send(new Request(candidate));

  public Task<HttpResponseMessage> send(Request req)
  {
    if (string.IsNullOrEmpty(remote_mailbox))
      throw new InvalidOperationException("No remote id");
    return http.PostAsync(remote_mailbox, as_json(req), token);
  }

  private static async Task<Request> get(string mailbox, CancellationToken token, Func<bool> runWhile = null)
  {
    if (string.IsNullOrEmpty(mailbox))
      throw new InvalidOperationException("null or empty mailbox");
    while (!token.IsCancellationRequested && (runWhile==null || runWhile()))
    {
      try
      {
        var request = await http.GetAsync(mailbox, token);
        if (request.IsSuccessStatusCode)
          return JsonUtility.FromJson<Request>(await request.Content.ReadAsStringAsync());
      }
      catch (HttpRequestException)
      {
      }
      catch (TaskCanceledException e)
      {
        throw e;
      }
      await Task.Delay(500);
    }
    return null;
  }
}
