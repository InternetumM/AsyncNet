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
* Easy to use TCP server
* Easy to use TCP client
* SSL support
* Custom protocol deframing / defragmentation support
* Switching protocols at any time
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

### Implementing custom protocol on top of TCP
This library does not come with any particular protocol, but it lets you define one.

You can read more about why it is necessary to implement a protocol and apply framing techniques when using TCP [here](https://blog.stephencleary.com/2009/04/message-framing.html).

Protocol defined below is just an example. 
You can come up with any protocol you want implementing `IProtocolFrameDefragmenter` or just defragmentation strategies: 
* `ILengthPrefixedDefragmentationStrategy` together with predefined `LengthPrefixedDefragmenter`
* `IMixedDefragmentationStrategy` together with predefined `MixedDefragmenter`

#### Deframing (defragmnetation)
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

#### Message codec
Remeber that you have to "encode" (prepend your message with it's length plus the length of the integer) any outgoing messages and "decode" (skip the frame header - four byte integer) any incoming frames to get your message. This simple class here will do the job:
```csharp
public class MessageCodec
{
	public byte[] Encode(byte[] data)
	{
		using (var ms = new MemoryStream())
		using (var bw = new BinaryWriter(ms))
		{
			bw.Write(data.Length + sizeof(int));
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
