# AsyncNet
Asynchronous network library for .NET
## Documentation
[Wiki](https://github.com/bartlomiej-stys/AsyncNet/wiki)
## Purpose
The primary purpose of this library is to provide easy to use interface for TCP and UDP networking in C#
## Getting started
This repository contains multiple projects that fall into different category. See below.
## AsyncNet.Tcp
### Installation
[NuGet](https://www.nuget.org/packages/AsyncNet.Tcp/)
### Features:
* [Easy to use TCP server](#tcp-server)
* [Easy to use TCP client](#tcp-client)
* [Custom protocol deframing / defragmentation support](#implementing-custom-protocol)
* [Switching protocols at any time](#switching-protocols)
* [SSL support](#ssl-support)
### Basic Usage
#### TCP Server
```csharp
var server = new AsyncNetTcpServer(7788);
server.ServerStarted += (s, e) => Console.WriteLine($"Server started on port: " +
    $"{e.ServerPort}");
server.ConnectionEstablished += (s, e) =>
{
    var peer = e.RemoteTcpPeer;
    Console.WriteLine($"New connection from [{peer.IPEndPoint}]");

    var hello = "Hello from server!";
    var bytes = System.Text.Encoding.UTF8.GetBytes(hello);
    peer.Post(bytes);
};
server.FrameArrived += (s, e) => Console.WriteLine($"Server received: " +
    $"{System.Text.Encoding.UTF8.GetString(e.FrameData)}");
await server.StartAsync(CancellationToken.None);
```
#### TCP Client
```csharp
var client = new AsyncNetTcpClient("127.0.0.1", 7788);
client.ConnectionEstablished += (s, e) =>
{
    var peer = e.RemoteTcpPeer;
    Console.WriteLine($"Connected to [{peer.IPEndPoint}]");

    var hello = "Hello from client!";
    var bytes = System.Text.Encoding.UTF8.GetBytes(hello);
    peer.Post(bytes);
};
client.FrameArrived += (s, e) => Console.WriteLine($"Client received: " +
    $"{System.Text.Encoding.UTF8.GetString(e.FrameData)}");
await client.StartAsync(CancellationToken.None);
```
#### Awaitaible TCP Client
```csharp
var client = new AsyncNetTcpClient("127.0.0.1", 7788);

using (var awaitaibleClient = new AwaitaibleAsyncNetTcpClient(client))
{
    try
    {
        var awaitaiblePeer = await awaitaibleClient.ConnectAsync();

        var hello = "Hello from client!";
        var bytes = System.Text.Encoding.UTF8.GetBytes(hello);

        await awaitaiblePeer.RemoteTcpPeer.SendAsync(bytes);
        var response = await awaitaiblePeer.ReadFrameAsync();

        Console.WriteLine($"Client received: " +
            $"{System.Text.Encoding.UTF8.GetString(response)}");
    }
    catch (Exception ex)
    {
        Console.WriteLine(ex);
        return;
    }
}
```

### Implementing custom protocol
This library does not come with any particular protocol, but it lets you define one.

You can read more about why it is necessary to implement a protocol and apply framing techniques when using TCP [here](https://blog.stephencleary.com/2009/04/message-framing.html).

Protocol defined below is just an example. 
You can come up with any protocol you want implementing `IProtocolFrameDefragmenter` or just defragmentation strategies: 
* `ILengthPrefixedDefragmentationStrategy` and put it into predefined `LengthPrefixedDefragmenter`
* `IMixedDefragmentationStrategy` and put it into predefined `MixedDefragmenter`

#### Deframing (defragmentation)
Let's start with defragmentation (deframing) strategy:

```csharp
public class MyDefragmentationStrategy : ILengthPrefixedDefragmentationStrategy
{
	public int GetFrameLength(byte[] data)
	{
		return BitConverter.ToInt32(data, 0);
	}

	public int FrameHeaderLength { get; } = sizeof(int);
}

// Making sure that frame defragmenter is created only once,
// because factory method is called for each received chunk of data
var lazyFactory = new Lazy<IProtocolFrameDefragmenter>(() => 
	new LengthPrefixedDefragmenter(new MyDefragmentationStrategy()));

// Server code
var server = new AsyncNetTcpServer(new AsyncNetTcpServerConfig
{
	ConnectionTimeout = TimeSpan.FromMinutes(5),
	Port = 5555,
	IPAddress = IPAddress.Parse("127.0.0.1"),
	ProtocolFrameDefragmenterFactory = _ => lazyFactory.Value
});

// Client code
var client = new AsyncNetTcpClient(new AsyncNetTcpClientConfig
{
	ConnectionTimeout = TimeSpan.FromMinutes(5),
	TargetPort = 5555,
	TargetHostname = "127.0.0.1",
	ProtocolFrameDefragmenterFactory = _ => lazyFactory.Value
});
```
`MyDefragmentationStrategy` expects that a four byte integer is present at the beginning of each frame. This integer is a value that determines the entire frame length - which is four bytes for frame header (integer length) plus a payload length (your message length). With this strategy it is possible to send and receive frames of any size, but it is recommended that your frames aren't too big.

#### Framing with message codec
Remeber that you have to encode/frame (prepend your message with a four byte integer) any outgoing messages and decode (skip the frame header - four byte integer) any incoming frames to get your message. This simple class here will do the job:
```csharp
public class MessageCodec
{
	public byte[] Encode(byte[] data)
	{
		using (var ms = new MemoryStream())
		using (var bw = new BinaryWriter(ms))
		{
			bw.Write(sizeof(int) + data.Length);
			bw.Write(data, 0, data.Length);

			return ms.ToArray();
		}
	}

	public byte[] Decode(byte[] frame)
	{
		using (var ms = new MemoryStream(frame))
		using (var br = new BinaryReader(ms))
		{
			var dataLength = br.ReadInt32() - sizeof(int);

			return br.ReadBytes(dataLength);
		}
	}
}
```

`MessageCodec` usage:

```csharp
MessageCodec _codec = new MessageCodec();

// Encoding
var myMessage = new byte[] { 65, 65, 65 };
var frame = _codec.Encode(myMessage);
peer.Post(frame);

// Decoding
serverOrClient.FrameArrived += (sender, args) =>
{
	var myMessage = _codec.Decode(args.FrameData);
	// do something with your message
};
```

That's it.

### Switching protocols

It is sometimes needed to switch a protocol to different one on particular peer at some point in time. 
You can do that via `IRemoteTcpPeer.SwitchProtocol`.

```csharp
Lazy<IProtocolFrameDefragmenter> _otherProtocolFactory = new Lazy<IProtocolFrameDefragmenter>(() => 
	new LengthPrefixedDefragmenter(new MyOtherDefragmentationStrategy()));

// at some point in time
peer.SwitchProtocol(_otherProtocolFactory.Value);
```

### SSL support
If you look at the config classes, you can configure SSL / TLS support there:

#### Client

```csharp
public class AsyncNetTcpClientConfig
{
	public Func<IRemoteTcpPeer, IProtocolFrameDefragmenter> ProtocolFrameDefragmenterFactory { get; set; } = (_) => MixedDefragmenter.Default;

	public string TargetHostname { get; set; }

	public int TargetPort { get; set; }

	public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.Zero;

	public int MaxSendQueueSize { get; set; } = -1;

	public Action<TcpClient> ConfigureTcpClientCallback { get; set; }

	public Func<IPAddress[], IEnumerable<IPAddress>> FilterResolvedIpAddressListForConnectionCallback { get; set; }

	public bool UseSsl { get; set; }

	public IEnumerable<X509Certificate> X509ClientCertificates { get; set; }

	public RemoteCertificateValidationCallback RemoteCertificateValidationCallback { get; set; } = (_, __, ___, ____) => true;

	public LocalCertificateSelectionCallback LocalCertificateSelectionCallback { get; set; }

	public EncryptionPolicy EncryptionPolicy { get; set; } = EncryptionPolicy.RequireEncryption;

	public bool CheckCertificateRevocation { get; set; }

	public SslProtocols EnabledProtocols { get; set; } = SslProtocols.Default;
}
```

#### Server

```csharp
public class AsyncNetTcpServerConfig
{
	public Func<IRemoteTcpPeer, IProtocolFrameDefragmenter> ProtocolFrameDefragmenterFactory { get; set; } = (_) => MixedDefragmenter.Default;

	public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.Zero;

	public int MaxSendQueuePerPeerSize { get; set; } = -1;

	public IPAddress IPAddress { get; set; } = IPAddress.Any;

	public int Port { get; set; }

	public Action<TcpListener> ConfigureTcpListenerCallback { get; set; }

	public bool UseSsl { get; set; }

	public X509Certificate X509Certificate { get; set; }

	public RemoteCertificateValidationCallback RemoteCertificateValidationCallback { get; set; } = (_, __, ___, ____) => true;

	public EncryptionPolicy EncryptionPolicy { get; set; } = EncryptionPolicy.RequireEncryption;

	public Func<TcpClient, bool> ClientCertificateRequiredCallback { get; set; } = (_) => false;

	public Func<TcpClient, bool> CheckCertificateRevocationCallback { get; set; } = (_) => false;

	public SslProtocols EnabledProtocols { get; set; } = SslProtocols.Default;
}
```

#### SSL config

You can pass this config to a client/server constructor. Set the `UseSsl` property to true and provide your SSL certificate - setting the `X509Certificate ` property. You can also set `EncryptionPolicy` or `EnabledProtocols` and override any of the callbacks to configure your certificate rules if you want. The client or server will use `SslStream` behind the scenes.

## AsyncNet.Udp
### Installation
[NuGet](https://www.nuget.org/packages/AsyncNet.Udp/)
### Features:
* Easy to use UDP server
* Easy to use UDP client
### Basic Usage
#### UDP Server
```csharp
var server = new AsyncNetUdpServer(7788);
server.ServerStarted += (s, e) => Console.WriteLine($"Server started on port: {e.ServerPort}");
server.UdpPacketArrived += (s, e) =>
{
    Console.WriteLine($"Server received: " +
        $"{System.Text.Encoding.UTF8.GetString(e.PacketData)} " +
        "from " +
        $"[{e.RemoteEndPoint}]");

    var response = "Response!";
    var bytes = System.Text.Encoding.UTF8.GetBytes(response);
    server.Post(bytes, e.RemoteEndPoint);
};
await server.StartAsync(CancellationToken.None);
```
#### UDP Client
```csharp
var client = new AsyncNetUdpClient("127.0.0.1", 7788);
client.ClientReady += (s, e) =>
{
    var hello = "Hello!";
    var bytes = System.Text.Encoding.UTF8.GetBytes(hello);

    e.Client.Post(bytes);
};
client.UdpPacketArrived += (s, e) =>
{
    Console.WriteLine($"Client received: " +
        $"{System.Text.Encoding.UTF8.GetString(e.PacketData)} " +
        "from " +
        $"[{e.RemoteEndPoint}]");
};
await client.StartAsync(CancellationToken.None);
```
