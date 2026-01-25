# ProtoTestTool Development Plan

## Goal
Create a standalone, dependency-free packet testing tool (`ProtoTestTool`) that can dynamically load serialization/deserialization logic via C# scripts. This removes the need to recompile the tool when protocols change and decouples it from specific project libraries like `NetworkClient` or `Protocol`.

## Core Requirements
1.  **No Direct Project References**: The tool must not reference `NetworkClient.csproj` or `Protocol.csproj`.
2.  **Scriptable Logic**: Packet serialization (object -> binary) and deserialization (binary -> object) must be defined in external C# scripts (`.csx` or `.cs`).
3.  **Dynamic Protocol Loading**: The tool should load message types (Protobuf) and handlers dynamically at runtime.

---

## Roadmap

### Phase 1: Project Setup (Current)
- [x] Create `ProtoTestTool` project (WPF).
- [x] Add dependencies:
    - `Microsoft.CodeAnalysis.CSharp.Scripting` (Roslyn)
    - `Google.Protobuf`
    - `NetCoreServer`
    - `Newtonsoft.Json`
- [ ] Ensure the project builds and runs.

### Phase 2: Script Interface Definition (Contract)
Define the interfaces that the external script must implement. The tool will communicate with the script through these contracts.

- Define `IPacketSerializer`:
    - `byte[] Serialize(object packet)`
    - `object Deserialize(byte[] buffer)`
    - `int GetHeaderSize()`
- Define `IPacketRegistry`:
    - `IEnumerable<Type> GetMessageTypes()`
    - `Type GetMessageType(int id)`

### Phase 3: Script Loader Implementation
Implement the logic to load and compile C# scripts at runtime.

- Create `ScriptLoader` class.
- Configure `ScriptOptions` to allow reference to `Google.Protobuf` and other necessary assemblies.
- Implement methods to compile the script and return the interface implementations (`IPacketSerializer`, etc.).

### Phase 4: Lightweight Network Client
Re-implement a basic TCP client without using the original `NetworkClient` library.

- Create `LightWeightClient` using `NetCoreServer`.
- Hook up the `Connect`, `Send`, `Disconnect` events.
- On `Received`: Pass the raw binary buffer to the Script's `IPacketSerializer.Deserialize`.

### Phase 5: UI Integration
Connect the backend logic to the WPF UI.

- **Script Selection**: UI to browse and select the `.csx` file.
- **Reload Button**: Re-compile the script without restarting the tool.
- **Packet Browser**: List available packets (obtained from Script's `IPacketRegistry`).
- **Property Grid / JSON Editor**: Edit packet fields (using `Newtonsoft.Json` for display).
- **Log View**: Display sent/received packets and connection status.

### Phase 6: Example Script Implementation
Provide a reference script (`PacketHandler.csx`) that replicates the current project's behavior.

- Implement the `Header` structure reading (Size, MsgId, etc.).
- Implement Protobuf parsing logic.
- Demonstrate how to map Message IDs to Types.

---

## Architecture Diagram

```mermaid
graph TD
    User[User] -->|Selects Script| UI[WPF UI]
    UI -->|Loads| SL[ScriptLoader]
    SL -->|Compiles| Script[C# Script (.csx)]
    
    UI -->|Connects| Net[LightWeightClient]
    
    User -->|Click Send| UI
    UI -->|1. Object| Script
    Script -->|2. Binary| Net
    Net -->|3. TCP Send| Server[Remote Server]
    
    Server -->|4. TCP Recv| Net
    Net -->|5. Binary| Script
    Script -->|6. Object| UI
```
