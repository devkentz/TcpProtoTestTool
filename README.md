# ProtoTestTool

A scriptable, lightweight packet testing tool for TCP protocols using Protobuf.
This tool allows you to define packet serialization and deserialization logic in C# scripts (`.csx`), removing the need to recompile the tool when protocols change.

## Features
- **Zero Project Dependencies**: Does not reference `NetworkClient` or `Protocol` projects directly.
- **Dynamic Scripting**: Logic is loaded from `.csx` files at runtime.
- **WPF UI**: Simple interface to connect, send, and view packets.

## Getting Started

1. **Build the Tool**:
   Open the solution and build `ProtoTestTool`.

2. **Prepare the Script**:
   The `PacketHandler.csx` file is the entry point.
   **Crucial**: You must update the `dllPaths` array in `PacketHandler.csx` to point to your actual compiled Protocol DLLs.

   ```csharp
   var dllPaths = new[]
   {
       @"C:\Work\MyProject\Protocol\bin\Debug\net9.0\Protocol.dll",
       // ... other dlls
   };
   ```

3. **Run**:
   - Start `ProtoTestTool.exe`.
   - Click **Load Script** (default path is `PacketHandler.csx`).
   - If loaded successfully, the "Available Packets" list will populate.
   - Enter IP/Port and click **Connect**.
   - Select a packet, edit the JSON payload, and click **Send**.

## Script Contract

Your script must return an object implementing `IScriptContext`.

```csharp
public interface IScriptContext
{
    IPacketSerializer Serializer { get; }
    IPacketRegistry Registry { get; }
    void Initialize();
}
```

See `ScriptContract` folder for details.

```
