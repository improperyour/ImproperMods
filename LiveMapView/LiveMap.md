## Phase 1: Research & Design
### 1.1 Game Data Investigation
- Reverse engineer Valheim's map data format and storage location
- Identify player position data structures and update frequency
- Document exploration state (fog of war) data format
- Research pin/marker data structures and limitations
### 1.2 Architecture Design
- Define mod-to-backend communication protocol (REST API, WebSocket, or hybrid)
- Design data synchronization strategy (polling interval, event-driven, or delta updates)
- Choose web stack (Flask/FastAPI for backend, React/vanilla JS for frontend)
- Plan map rendering approach (Canvas, WebGL, or tile-based with Leaflet/similar)
#### 1.2.1 Update Strategy: Push on Configurable Interval
- Mod pushes updates every N seconds (configurable, default 5-10s)
- Only when at least one player is online
- Idle detection: Stop updates after X minutes of no player movement (optional optimization)
#### 1.2.2. Authentication: API Key
- Single shared API key configured in mod config file
- Backend validates key on each request
- Simple header-based auth: `X-API-Key: <token>`
- Web client gets read-only access (no auth or separate viewer token)
#### 1.2.3. Map Tiles: Hybrid Approach
- **Phase 1 (MVP)**: Use pre-generated base map tiles from game assets
- **Architecture**: Design tile endpoint with `/tiles/{z}/{x}/{y}.png` structure
- **Future**: Hook allows switching to dynamic generation (biomes, resource overlays)
#### 1.2.4. Coordinate System: Proposed Solution
##### Problem
Valheim uses a centered coordinate system where (0,0) is world center. Map likely spans roughly -10000 to +10000 on each axis (20km world).
###### Proposed Approach
**Two-layer system:**
1. **Internal Storage**: Keep native Valheim coordinates
    - Store all data in Valheim's coordinate space
    - Easier to debug, matches game console output
2. **Web Map Translation**: Convert to tile/pixel coordinates on frontend
````python
   # Backend provides world bounds in config endpoint
   world_bounds = {
       "min_x": -10500,
       "max_x": 10500,
       "min_z": -10500,  # Valheim uses Z for north/south
       "max_z": 10500,
       "map_size": 21000  # total size in game units
   }
   
   # Frontend converts for display
   def valheim_to_pixel(x, z, zoom_level):
       tile_size = 256  # standard tile size
       map_size_pixels = 2048 * (2 ** zoom_level)  # base map at zoom 0
       
       # Normalize to 0-1 range
       norm_x = (x - world_bounds["min_x"]) / world_bounds["map_size"]
       norm_z = (z - world_bounds["min_z"]) / world_bounds["map_size"]
       
       # Convert to pixel space
       pixel_x = norm_x * map_size_pixels
       pixel_y = (1 - norm_z) * map_size_pixels  # flip Y axis
       
       return (pixel_x, pixel_y)
```

### Benefits
- No coordinate conversion in mod (minimal overhead)
- Backend stays in native game coordinates
- Easy to add world seed variations later
- Tile system remains standard (zoom 0, 1, 2, etc.)

---
````
#### 1.2.5. Hosting Model: Co-located with Game Server
- Backend runs on same machine as Valheim server
- Use `localhost` or `127.0.0.1` for mod-to-backend communication
- Web interface exposed on LAN/WAN port (configurable, default 8080)
### 1.3 Feature Scope for MVP
- Core: Real-time player positions and explored areas
- Core: Existing map pins/markers display
- Optional for MVP: Death markers, portal locations
- Defer: Custom pins from web interface, biome overlays, boss status
---
## Phase 2: Mod Development (BepInEx Plugin)
### 2.1 Basic Mod Setup
- Initialize BepInEx plugin project structure
- Implement configuration system (update intervals, server URL, auth tokens)
- Create logging framework for debugging
#### 2.1.1: Initialize BepInEx Plugin Project
**Prerequisites**: None  
**Deliverable**: Buildable plugin stub
- Create Visual Studio C# class library project targeting .NET Framework 4.6.2
- Add BepInEx NuGet packages (BepInEx.Core, BepInEx.PluginInfoAttribute)
- Reference Valheim assemblies (assembly_valheim.dll, assembly_utils.dll)
- Create main plugin class with `[BepInPlugin]` attribute
- Implement `BaseUnityPlugin` with basic `Awake()` method
- Add build configuration to output to BepInEx plugins folder
- Test: Plugin loads and logs "Awake" message in BepInEx console
#### 2.1.2: Configuration System
**Prerequisites**: 2.1.1  
**Deliverable**: Config file with all settings
- Implement BepInEx `ConfigFile` integration
- Add configuration entries:
    - `BackendURL` (string, default: "[http://localhost:8080](http://localhost:8080)")
    - `APIKey` (string, default: "change-me")
    - `UpdateInterval` (int, default: 5, range 1-60)
    - `IdleThreshold` (int, default: 60, seconds before considering idle)
    - `IdleUpdateInterval` (int, default: 30)
    - `EnableLogging` (bool, default: true)
    - `LogVerbosity` (enum: Minimal/Normal/Verbose)
- Create config reload mechanism
- Test: Config file generates on first run, values load correctly
#### 2.1.3: Logging Framework
**Prerequisites**: 2.1.2  
**Deliverable**: Logging utility class
- Create `MapLogger` wrapper class around BepInEx logger
- Implement verbosity level filtering
- Add context tags (e.g., [DATA], [NETWORK], [HOOK])
- Add debug dump methods for complex objects
- Create performance timing helpers
- Test: Log messages appear correctly filtered by verbosity
### 2.2 Map Data Extraction
- Hook into Minimap class to extract explored area data
- Serialize fog-of-war data to transferable format
- Extract current world seed and bounds
- Capture existing pins with types, positions, and metadata
#### 2.2.1: Hook Into Minimap System
**Prerequisites**: 2.1.1  
**Deliverable**: Working hook into Minimap updates
- Research Minimap class structure using dnSpy/ILSpy
- Identify explored area data structure (likely bool[] or texture)
- Use Harmony to patch `Minimap.Awake()` or `Minimap.UpdateExplore()`
- Store reference to Minimap instance
- Add null checks and error handling
- Test: Log when explored area changes
#### 2.2.2: Extract Fog-of-War Data
**Prerequisites**: 2.2.1  
**Deliverable**: Serializable exploration data structure
- Determine chunk/sector size for exploration data
- Create data structure:
  csharp

```csharp
  class ExploredChunk {
      public int ChunkX;
      public int ChunkZ;
      public byte[] ExplorationMask; // or bitfield
  }
```
- Implement extraction from Minimap's internal storage
- Add delta tracking (only changed chunks since last update)
- Compress data if needed (RLE or simple bit packing)
- Test: Export current explored state, verify coverage
#### 2.2.3: Extract World Metadata
**Prerequisites**: 2.2.1  
**Deliverable**: World info data structure
- Get world seed from `World.GetWorldSeed()`
- Calculate/retrieve world bounds (likely ±10500)
- Get world name
- Create `WorldInfo` class:
```csharp
  class WorldInfo {
      public long Seed;
      public string Name;
      public int MinX, MaxX, MinZ, MaxZ;
  }
```
- Cache this data (only send on first update or world change)
- Test: Verify world info matches game console output
#### 2.2.4: Extract Map Pins
**Prerequisites**: 2.2.1  
**Deliverable**: Pin data extraction and serialization
- Hook into `Minimap.m_pins` list
- Identify pin types (enum or constants)
- Create `MapPin` class:
```csharp
  class MapPin {
      public string Id; // unique identifier
      public int Type;
      public float X, Y, Z;
      public string Name;
      public bool IsChecked;
  }
```
- Implement change detection (additions/removals)
- Handle death markers separately (they're time-limited)
- Test: Place pins in game, verify extraction
### 2.3 Player Tracking
- Hook player position updates efficiently
- Track multiple players in multiplayer sessions
- Include player name, position, rotation, and status (alive/ghost)
#### 2.3.1: Hook Player Position Updates
**Prerequisites**: 2.1.1  
**Deliverable**: Real-time player position tracking
- Research Player class update methods
- Hook `Player.Update()` or use `FixedUpdate()` with position caching
- Get player transform (position and rotation)
- Implement position change threshold (only track if moved >0.1 units)
- Handle player instance lifecycle (spawn/despawn)
- Test: Log player position every second, verify accuracy
#### 2.3.2: Track Multiple Players
**Prerequisites**: 2.3.1  
**Deliverable**: Multi-player position tracking
- Get all active players from `Player.GetAllPlayers()`
- Create `PlayerState` class:
```csharp
  class PlayerState {
      public long PlayerId;
      public string Name;
      public float X, Y, Z;
      public float Rotation; // yaw/heading
      public bool IsAlive;
      public long LastUpdate; // timestamp
  }
```
- Maintain dictionary of active players
- Remove disconnected players after timeout
- Test: Run with 2+ players, verify all tracked
#### 2.3.3: Idle Detection
**Prerequisites**: 2.3.2, 2.1.2  
**Deliverable**: Idle state tracking per player
- Track last position and timestamp per player
- Calculate movement delta each update
- Flag player as idle if stationary for `IdleThreshold` seconds
- Create aggregate idle state (all players idle = reduce updates)
- Add forced update override (button press, menu interaction)
- Test: Stand still, verify idle detection, move, verify resume
### 2.4 Data Transmission
- Implement HTTP client for sending data to backend
- Add retry logic and connection failure handling
- Support configurable update intervals (default 5-10 seconds)
- Batch updates to minimize network overhead
````ini
### Mod Push Endpoint
```
POST /api/map/update
Headers: X-API-Key: <key>
Body: {
  "timestamp": 1234567890,
  "players": [
    {"name": "Player1", "x": 100, "y": 50, "z": -200, "rotation": 45, "alive": true}
  ],
  "explored_chunks": [...],  // Delta updates only
  "pins": [...]  // Only if changed
}
````
### Phase 2.4b: Connection Management
- Implement health check ping every 30s when no updates needed
- Add reconnection logic with exponential backoff
- Log connection status to BepInEx console
#### 2.4.1: HTTP Client Setup
**Prerequisites**: 2.1.2  
**Deliverable**: Working HTTP client with auth
- Create `BackendClient` class using `UnityWebRequest` or `HttpClient`
- Implement API key header injection
- Add timeout configuration (default 10s)
- Create base request wrapper with error handling
- Add user-agent header with mod version
- Test: Send test request to mock server
#### 2.4.2: JSON Serialization
**Prerequisites**: 2.4.1, 2.2.2, 2.2.4, 2.3.2  
**Deliverable**: Serializable payload structure
- Add Newtonsoft.Json or use Unity's JsonUtility
- Create master `MapUpdatePayload` class:
````csharp
  class MapUpdatePayload {
      public long Timestamp;
      public WorldInfo World; // only on first update
      public List<PlayerState> Players;
      public List<ExploredChunk> ExploredChunks; // delta
      public List<MapPin> PinsAdded;
      public List<string> PinsRemoved; // IDs
  }
````
- Implement serialization/deserialization
- Add payload size logging
- Test: Serialize sample data, verify JSON structure
#### 2.4.3: Update Loop Implementation
**Prerequisites**: 2.4.1, 2.4.2, 2.3.3  
**Deliverable**:  Coroutine-based update scheduler
- Implement coroutine or timer-based update loop
- Start loop when first player connects
- Use `UpdateInterval` or `IdleUpdateInterval` based on state
- Collect all changed data since last update
- Send to `/api/map/update` endpoint
- Stop loop when last player disconnects
- Test: Verify timing accuracy, no memory leaks
#### 2.4.4: Error Handling & Retry Logic
**Prerequisites**: 2.4.3  
**Deliverable**: Robust network error handling
- Catch and log network exceptions (timeout, connection refused, etc.)
- Implement exponential backoff for retries (1s, 2s, 4s, 8s, max 30s)
- Queue failed updates (max 10 in queue, drop oldest)
- Add connection state tracking (Connected/Reconnecting/Failed)
- Log connection state changes
- Reset retry counter on successful update
- Test: Simulate backend downtime, verify retry behavior
#### 2.4.5: Health Check Ping
**Prerequisites**: 2.4.4  
**Deliverable**: Keep-alive mechanism
- Implement `/api/health` endpoint ping
- Send ping every 30s when no data updates needed
- Use ping to detect backend availability
- Update connection state based on ping success
- Include mod version in ping payload
- Test: Stop backend, verify detection and recovery
## 2.5: Integration & Optimization
#### 2.5.1: Performance Profiling
**Prerequisites**: All Task 2.2, 2.3, 2.4  
**Deliverable**: Performance metrics and optimization
- Add timing measurements around key operations:
- Data extraction time - Serialization time
- Network request time
- Total update cycle time
- Log performance metrics (only in verbose mode)
- Identify bottlenecks (target <50ms per update cycle)
- Optimize heavy operations (caching, lazy evaluation)
- Test: Run with single player for 30min, check frame impact
#### 2.5.2: Memory Management
**Prerequisites**: 2.5.1  
**Deliverable**: Memory leak prevention
- Implement proper disposal of HTTP requests/responses
- Clear old delta tracking data
- Limit cached data size (exploration chunks, pin history)
- Add periodic cleanup of stale data
- Monitor memory usage over extended play sessions
- Test: Play for 2+ hours, check memory stability 
#### 2.5.3: Thread Safety 
**Prerequisites**: All Task 2.4  
**Deliverable**: Thread-safe network operations
- Ensure network calls don't block main thread
- Use async/await or background threads properly
- Protect shared data structures with locks
- Handle race conditions in player list updates
- Test: Simulate concurrent updates, verify no crashes
### 2.6: User Interface & Debugging
#### 2.6.1: In-Game Status Display (Optional)
**Prerequisites**: 2.4.4  
**Deliverable**: Simple on-screen status indicator
- Create minimal UI overlay (optional, can use BepInEx console)
- Show connection state icon
- Display last update timestamp
- Show update queue size if any failures
- Add toggle key to show/hide
- Test: Verify visibility and updates
#### 2.6.2: Debug Commands
**Prerequisites**: All previous tasks  
**Deliverable**: Console commands for testing
- Add BepInEx console commands:
    - `webmap.status`   - Show current state
    - `webmap.force_update`   - Send immediate update
    - `webmap.reconnect`   - Force reconnection
    - `webmap.dump_data`   - Export current data to file
- Test: Verify each command works
#### 2.6.3: Error Reporting
**Prerequisites**: All previous tasks  
**Deliverable**: User-friendly error messages
- Create error message catalog with solutions:
- "Backend unreachable" → check URL/firewall
- "Invalid API key" → check config
- "Rate limited" → reduce update frequency
- Log errors with actionable advice
- Add error state recovery instructions
- Test: Trigger each error condition
### Testing Checklist Per Task Each task should verify:
- ✅ Code compiles without warnings
- ✅ No exceptions in normal operation
- ✅ BepInEx logs show expected messages
- ✅ Memory usage is stable
- ✅ Performance impact <5ms per frame
---
## Phase 3: Backend Development
### 3.1 API Server Setup
- Create Flask/FastAPI application structure
- Implement authentication for mod connections
- Set up CORS for web client access
#### Phase 3.1b: Server Configuration
- Add systemd service file for Linux deployments
- Create Windows service wrapper option
- Include firewall configuration notes
### 3.2 Data Processing
- Design data models for map state, players, and pins
- Implement endpoints for receiving mod updates
- Create in-memory cache or lightweight DB (Redis/SQLite) for current state
- Add data validation and sanitization
### 3.3 WebSocket Support
- Implement WebSocket server for real-time updates to web clients
- Broadcast position updates to connected clients
- Handle client connection lifecycle
### 3.4 Static File Serving
- Serve web frontend files
- Provide map tile generation endpoints if needed
---
## Phase 4: Frontend Development
### 4.1 Map Rendering
- Implement canvas-based or library-based map renderer
- Add zoom and pan controls
- Render fog-of-war overlay (explored vs unexplored)
- Optimize rendering for large maps
### 4.2 Player Display
- Show real-time player positions with icons/markers
- Display player names and status
- Add smooth position interpolation between updates
- Show player facing direction
### 4.3 Pins & Markers
- Render game pins with appropriate icons
- Implement filtering by pin type
- Add tooltip/info display on hover/click
### 4.4 UI/UX
- Create minimalist control panel
- Add legend for symbols and markers
- Implement mobile-responsive design
- Add connection status indicator
### Phase 4.4b: Network Status
- Show "Server Offline" state gracefully
- Display last update timestamp
- Add connection latency indicator
---
## Phase 5: Integration & Testing
### 5.1 End-to-End Testing
- Test mod with single player server
- Test with multiple concurrent players
- Verify data accuracy (positions, explored areas match in-game)
- Stress test update intervals and network conditions
### 5.2 Performance Optimization
- Profile mod performance impact on game
- Optimize backend data processing
- Minimize frontend rendering overhead
- Tune update intervals for smooth experience
### 5.3 Error Handling
- Test network disconnection scenarios
- Verify graceful degradation when backend unavailable
- Add user-friendly error messages
### 5.4 Coordinate System Testing Strategy
1. **Calibration Phase**: Have mod send known landmark positions (spawn, bosses)
2. **Verification**: Check web map displays them correctly
3. **Edge Cases**: Test at world boundaries (±10500 range)
4. **Rotation**: Verify player facing direction renders correctly
---
## Phase 6: Documentation & Deployment
### 6.1 Documentation
- Write installation guide for mod (BepInEx setup)
- Document backend deployment options (local/VPS)
- Create configuration examples
- Add troubleshooting section
### 6.2 Packaging
- Create mod release package
- Include default configuration file
- Package backend with requirements.txt or Docker
- Bundle frontend assets