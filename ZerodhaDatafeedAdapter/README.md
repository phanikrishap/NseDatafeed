# ZerodhaDatafeedAdapter

A high-performance NinjaTrader 8 adapter for connecting to the Zerodha trading platform, enabling real-time market data, historical data, Option Chain analysis, and trading operations for Indian markets.

## Overview

ZerodhaDatafeedAdapter serves as a bridge between NinjaTrader 8 and the Zerodha trading platform. It provides access to Indian market data (NSE, BSE) through Zerodha's Kite Connect API, with specialized features for options trading including a Market Analyzer with Option Chain visualization and synthetic straddle instruments.

## Features

### Core Features
- **Real-time Market Data**: High-performance WebSocket streaming via Zerodha's Kite Connect
- **Historical Data**: Minute/Daily bars with intelligent caching
- **Market Depth (Level 2)**: Full order book data
- **Multi-Asset Support**: Stocks, Futures, Options, Indices (NIFTY 50, SENSEX, GIFT NIFTY)

### Option Chain & Market Analyzer
- **Option Chain Window**: Real-time CE/PE prices with straddle calculations
- **Synthetic Straddle Instruments**: Trade straddle spreads as single instruments
- **Projected Open Calculations**: Uses GIFT NIFTY to project market opens
- **0DTE/1DTE Selection**: Automatic selection of optimal expiry based on DTE

### TBS Manager (Time-Based Straddle)
- **Excel-Based Configuration**: Read straddle configs from `tbsConfig.xlsx`
- **Multi-Tranche Support**: Multiple entry/exit times per day
- **Execution Dashboard**: Real-time monitoring of straddle positions
- **Simulated P&L Tracking**: Track theoretical P&L without live execution

### Performance Optimizations
- **OptimizedTickProcessor**: Lock-free concurrent tick processing with sharded parallelism
- **SharedWebSocketService**: Single WebSocket connection shared across all subscriptions
- **Multi-Callback Support**: Multiple subscribers (Chart, Option Chain, Market Analyzer) can receive the same tick data
- **Intelligent Throttling**: UI updates throttled to prevent overwhelming the display

### Modern Async Architecture (v2.1)

The adapter uses modern event-driven patterns to eliminate "Sleep & Hope" anti-patterns:

**Phase 1: TPL Dataflow Pipeline (SubscriptionManager)**
- Replaces `ConcurrentQueue + Task.Run` with `ActionBlock` pipeline
- 3-stage pipeline: Subscription → Historical → Streaming
- Built-in backpressure via `BoundedCapacity`
- Rate limiting via `SemaphoreSlim` for Zerodha API limits
- Event-driven completion via `TaskCompletionSource`

**Phase 2: Rx.NET Reactive Streams (OptimizedTickProcessor)**
- `IObservable<TickStreamItem>` for tick data streams
- `ThrottledOptionTickStream` - 100ms sampling for UI (no polling loops)
- `OptionTickStream` - unthrottled for trading logic
- Per-symbol streams via `GetSymbolStream(symbol)`

**Phase 3: Connection State Machine (SharedWebSocketService)**
- Formal state machine: `Disconnected → Connecting → Connected → BackingOff → Reconnecting`
- Exponential backoff: 1s → 2s → 4s → 8s → 16s (capped)
- TCS-based waiting replaces polling loops
- Centralized timeout constants (no magic numbers)

### Reliability & Diagnostics
- **TaskCompletionSource Pattern**: Robust async signaling for token/connection ready states - late subscribers never miss events
- **Event-Driven Token Validation**: WebSocket waits for valid token before connecting
- **State Machine Transitions**: Proper state management in SharedWebSocketService with safe transitions
- **Dedicated Startup Logger**: Critical startup events logged to separate file for easy diagnosis
- **Atomic Flag Operations**: All processing flags use `Interlocked.CompareExchange` to prevent race conditions
- **Event-Driven Option Prices**: `OptionTickReceived` event ensures reliable price updates regardless of subscription timing

## Architecture

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                              NinjaTrader 8                                   │
├─────────────────────────────────────────────────────────────────────────────┤
│                              ZerodhaAdapter                                       │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐ │
│  │ L1Subscription│  │ Connector   │  │ Instruments │  │ MarketAnalyzer AddOn│ │
│  │ (Multi-Callback)│ │             │  │ Service     │  │ (Option Chain UI)  │ │
│  └──────┬──────┘  └──────┬──────┘  └──────┬──────┘  └──────────┬──────────┘ │
├─────────┼────────────────┼────────────────┼─────────────────────┼───────────┤
│         │                │                │                     │           │
│  ┌──────▼──────────────────────────────────────────────────────▼─────────┐  │
│  │                    Services Layer                                      │  │
│  │  ┌─────────────────┐  ┌─────────────────┐  ┌─────────────────────────┐ │  │
│  │  │ MarketDataService│  │HistoricalData   │  │ SubscriptionManager     │ │  │
│  │  │ (Tick/Depth)    │  │ Service         │  │ & OptionGeneration      │ │  │
│  │  └────────┬────────┘  └────────┬────────┘  └───────────┬─────────────┘ │  │
│  │           │                    │                       │               │  │
│  │  ┌────────▼────────────────────▼───────────────────────▼─────────────┐ │  │
│  │  │              OptimizedTickProcessor                                │ │  │
│  │  │  - Sharded parallel processing                                     │ │  │
│  │  │  - Lock-free concurrent dictionaries                               │ │  │
│  │  │  - Multi-callback invocation                                       │ │  │
│  │  └────────────────────────────┬──────────────────────────────────────┘ │  │
│  │                               │                                        │  │
│  │  ┌────────────────────────────▼──────────────────────────────────────┐ │  │
│  │  │              SharedWebSocketService                                │ │  │
│  │  │  - Single WebSocket connection                                     │ │  │
│  │  │  - Token-based subscription management                             │ │  │
│  │  │  - Automatic reconnection                                          │ │  │
│  │  └────────────────────────────┬──────────────────────────────────────┘ │  │
│  └───────────────────────────────┼──────────────────────────────────────────┘ │
│                                  │                                           │
├──────────────────────────────────┼───────────────────────────────────────────┤
│                                  ▼                                           │
│                        Zerodha Kite Connect API                              │
│                   (WebSocket + REST for Historical)                          │
└─────────────────────────────────────────────────────────────────────────────┘
```

### Key Components

| Component | Description |
|-----------|-------------|
| **ZerodhaAdapter** | Main NinjaTrader entry point - implemented as partial classes for modularity |
| **Connector** | Service orchestration hub and singleton access manager with lifecycle tracking |
| **L1Subscription** | Thread-safe multi-callback container supporting concurrent Chart/UI/Analyzer data |
| **OptimizedTickProcessor** | High-performance sharded processor with Rx.NET streams and tiered backpressure |
| **ShardProcessor** | Dedicated per-shard worker logic for isolated, high-speed tick processing |
| **SharedWebSocketService** | Advanced WebSocket manager using a StateMachine-driven connection lifecycle |
| **WebSocketStateController**| Formal FSM for connection states (Connecting, BackingOff, Reconnecting) |
| **WebSocketPacketParser** | Specialized binary parser for optimized Big-Endian packet decoding |
| **SubscriptionManager** | TPL Dataflow pipeline for throttled option chain and instrument subscriptions |
| **MarketAnalyzerLogic** | Core logic for projected opens and ATM tracking; hosts the centralized PriceHub |
| **OptionGenerationService**| Specialized service for dynamic option symbol and instrument generation |
| **OptionChainWindow** | Modular WPF UI for real-time option chain and straddle visualization |
| **TBSManagerWindow** | Modular dashboard for time-based straddle execution monitoring |
| **TBSConfigurationService**| Excel-driven strategy configuration and tranche management |
| **InstrumentManager** | Orchestrates symbol mapping and SQLite-based instrument caching |
| **TickCacheManager** | Efficiently manages last-known-tick caching and replay for new subscribers |
| **Health Monitors** | Specialized monitors for Memory, GC, and Processor performance |

### TPL Dataflow Pipeline (SubscriptionManager)

```
┌─────────────────────────────────────────────────────────────────────────────────┐
│                     TPL Dataflow Subscription Pipeline                           │
├─────────────────────────────────────────────────────────────────────────────────┤
│                                                                                  │
│  ┌─────────────────────┐     ┌─────────────────────┐     ┌────────────────────┐ │
│  │  _subscriptionBlock │ ──► │   _historicalBlock  │ ──► │  _streamingBlock   │ │
│  │  MaxParallelism=1   │     │   MaxParallelism=10 │     │  MaxParallelism=6  │ │
│  │  BoundedCapacity=500│     │   BoundedCapacity=200│    │  BoundedCapacity=100│ │
│  │  (Sequential for    │     │   (Rate limited via │     │  (BarsRequest +    │ │
│  │   UI thread safety) │     │    SemaphoreSlim)   │     │   VWAP setup)      │ │
│  └─────────────────────┘     └─────────────────────┘     └────────────────────┘ │
│           │                           │                           │              │
│           ▼                           ▼                           ▼              │
│   Create NT Instrument        Fetch Historical Data      Trigger BarsRequest    │
│   Subscribe WebSocket         Cache in SQLite            Process Straddles      │
│                                                                                  │
│  Benefits:                                                                       │
│  - Built-in backpressure (BoundedCapacity prevents memory overflow)             │
│  - No polling loops (event-driven completion)                                   │
│  - Proper async/await flow (no fire-and-forget Task.Run)                        │
│  - Automatic batching with MaxDegreeOfParallelism                               │
│  - Clean shutdown via Complete() + Completion task                              │
└─────────────────────────────────────────────────────────────────────────────────┘
```

### Rx.NET Reactive Streams (OptimizedTickProcessor)

```
┌─────────────────────────────────────────────────────────────────────────────────┐
│                        Reactive Tick Stream Architecture                         │
├─────────────────────────────────────────────────────────────────────────────────┤
│                                                                                  │
│  WebSocket Tick ──► CacheLastTick() ──► _tickSubject.OnNext(TickStreamItem)     │
│                                                │                                 │
│                                                ▼                                 │
│                          ┌─────────────────────────────────────┐                │
│                          │     Subject<TickStreamItem>         │                │
│                          │     (Hot Observable - every tick)   │                │
│                          └─────────────────┬───────────────────┘                │
│                                            │                                     │
│              ┌─────────────────────────────┼─────────────────────────────┐      │
│              │                             │                             │      │
│              ▼                             ▼                             ▼      │
│   ┌─────────────────────┐     ┌─────────────────────┐     ┌────────────────────┐│
│   │     TickStream      │     │   OptionTickStream  │     │ThrottledOptionStream││
│   │   (All ticks, 0ms)  │     │  (CE/PE only, 0ms)  │     │ (100ms per symbol) ││
│   │                     │     │                     │     │ GroupBy + Sample   ││
│   │ Use: Charts, TBS    │     │ Use: Trading logic  │     │ Use: Option Chain  ││
│   │      execution      │     │                     │     │      UI updates    ││
│   └─────────────────────┘     └─────────────────────┘     └────────────────────┘│
│                                                                                  │
│  Key Point: UI throttling does NOT affect trading execution!                    │
│  TBS stop-loss, entries, exits all receive every tick at full speed.            │
└─────────────────────────────────────────────────────────────────────────────────┘
```

### WebSocket Connection State Machine

```
┌─────────────────────────────────────────────────────────────────────────────────┐
│                    SharedWebSocketService State Machine                          │
├─────────────────────────────────────────────────────────────────────────────────┤
│                                                                                  │
│  ┌──────────────┐     TokenReady      ┌────────────────┐                        │
│  │ Disconnected │ ──────────────────► │   Connecting   │                        │
│  │    (0)       │                     │      (1)       │                        │
│  └──────────────┘                     └────────────────┘                        │
│         ▲                                    │                                   │
│         │                              Success│ Failure                          │
│         │                                    ▼    │                              │
│         │                             ┌──────────┐│                              │
│         │◄────────── Dispose ─────────│Connected ││                              │
│         │            (4)              │   (2)    ││                              │
│         │                             └──────────┘│                              │
│         │                                  │      │                              │
│         │                           Error  │      │                              │
│         │                                  ▼      ▼                              │
│         │                             ┌────────────────┐                         │
│         │◄──── MaxRetries ────────────│   BackingOff   │                         │
│         │                             │      (5)       │                         │
│         │                             └────────────────┘                         │
│         │                                    │                                   │
│         │                              Timer │ (1s→2s→4s→8s→16s)                 │
│         │                                    ▼                                   │
│         │                             ┌────────────────┐                         │
│         └─────────────────────────────│  Reconnecting  │                         │
│                                       │      (3)       │                         │
│                                       └────────────────┘                         │
│                                                                                  │
│  Timeout Constants (centralized, no magic numbers):                              │
│  - TOKEN_READY_TIMEOUT_MS = 30000                                               │
│  - CONNECTION_TIMEOUT_MS = 30000                                                │
│  - CLOSE_TIMEOUT_MS = 1000                                                      │
│  - BACKOFF_INITIAL_MS = 1000                                                    │
│  - BACKOFF_MAX_MS = 16000                                                       │
└─────────────────────────────────────────────────────────────────────────────────┘
```

### Data Flow

1. **WebSocket Tick** → `SharedWebSocketService` receives binary tick data
2. **Parse** → Tick data parsed into `ZerodhaTickData` object
3. **Queue** → Tick queued in `OptimizedTickProcessor`
4. **Process** → Sharded workers process ticks in parallel
5. **Cache & Event** → Tick cached in `_lastTickCache` AND `OptionTickReceived` event fired
6. **Callbacks** → ALL registered callbacks invoked (Chart, Option Chain, etc.)
7. **NinjaTrader** → Data fed to NinjaTrader's market data system

### Event-Driven Option Price Updates

The adapter uses a reliable event-driven architecture for option prices that bypasses callback chain timing issues:

```
WebSocket Tick
    │
    ▼
OptimizedTickProcessor.CacheLastTick()
    │
    ├───► Cache tick in _lastTickCache
    │
    └───► Fire OptionTickReceived event (for CE/PE symbols)
              │
              ▼
         SubscriptionManager.OnOptionTickReceived()
              │
              └───► Fire OptionPriceUpdated event
                         │
                         ▼
                    OptionChainWindow.OnOptionPriceUpdated()
```

**Why Event-Driven?**
- **Reliable**: No timing dependencies on callback registration order
- **Simple**: Direct event subscription, no complex callback chains
- **Robust**: Works regardless of when UI subscribes (pre-market, post-market, mid-session)

**Previous Problem Solved:**
The callback-based approach had timing issues where:
1. OptionChainTabPage registered callback #1 via `SubscribeForPersistence`
2. Tick arrived and was replayed to callback #1
3. SubscriptionManager registered callback #2 later
4. Callback #2 never received the tick (already marked as "replayed")

The event-driven approach fires `OptionTickReceived` for EVERY tick, ensuring all subscribers receive updates regardless of registration timing.

### Multi-Callback Architecture

The adapter supports multiple subscribers for the same symbol:

```
Symbol: SENSEX25DEC85500CE
├── Callback 1: SubscriptionManager → OptionPriceUpdated → Option Chain UI
├── Callback 2: NinjaTrader Chart → Updates chart bars
└── Callback 3: Market Analyzer → Stores to NT database
```

This is achieved through `L1Subscription.AddCallback()` which uses unique callback IDs rather than replacing callbacks.

### Price Distribution Hub (PriceHub)

The adapter implements a centralized price distribution hub to avoid duplicate WebSocket subscriptions across multiple UI panes:

```
┌─────────────────┐         ┌─────────────────┐
│  WebSocket      │────────>│ SubscriptionMgr │
│  (Zerodha)      │         │ OptionPriceUpd  │
└─────────────────┘         └────────┬────────┘
                                     │
                                     v
                            ┌────────────────────┐
                            │   Option Chain     │
                            │ (populates prices) │
                            └────────┬───────────┘
                                     │ UpdateOptionPrice()
                                     v
                            ┌────────────────────┐
                            │     PriceHub       │  <-- Single source of truth
                            │ (MarketAnalyzerLog)│
                            │                    │
                            │ - GetPrice(symbol) │
                            │ - GetATMStrike()   │
                            │ - PriceUpdated evt │
                            └────────┬───────────┘
                                     │ PriceUpdated event
                    ┌────────────────┼────────────────┐
                    v                v                v
            ┌───────────┐    ┌───────────┐    ┌───────────┐
            │TBS Manager│    │Future Pane│    │Future Pane│
            │(consumer) │    │(consumer) │    │(consumer) │
            └───────────┘    └───────────┘    └───────────┘
```

**Key Features:**
- **Single WebSocket Subscription**: Option Chain subscribes once, all consumers share the data
- **Centralized ATM Tracking**: `MarketAnalyzerLogic.GetATMStrike()` provides ATM to all consumers
- **Event-Driven Updates**: `PriceUpdated` event fires only when prices actually change
- **Thread-Safe Access**: All price operations are lock-protected

**Usage:**
```csharp
// Get current price (populated by Option Chain)
decimal price = MarketAnalyzerLogic.Instance.GetPrice("NIFTY25DEC25000CE");

// Get ATM strike (calculated by Option Chain from straddle prices)
decimal atm = MarketAnalyzerLogic.Instance.GetATMStrike("NIFTY");

// Subscribe to price updates
MarketAnalyzerLogic.Instance.PriceUpdated += (symbol, price) => {
    // Handle price update
};
```

## Requirements

- NinjaTrader 8 (8.1.x or later)
- .NET Framework 4.8
- Zerodha trading account with Kite Connect API access
- Valid API credentials (API Key, Secret, Access Token)

## Installation

1. Copy `ZerodhaDatafeedAdapter.dll` to `Documents\NinjaTrader 8\bin\Custom\`
2. Copy `ZerodhaAPI.dll` and dependencies to the same folder
3. Restart NinjaTrader
4. Configure the adapter in NinjaTrader's Connections settings

## Configuration

### Zerodha API Credentials
Configure in NinjaTrader adapter settings:
- **API Key**: Your Kite Connect API key
- **API Secret**: Your Kite Connect API secret
- **Access Token**: Generated daily via Zerodha login

### Logging Configuration

Logs are stored in: `Documents\NinjaTrader 8\ZerodhaAdapter\Logs\`

**Log Files:**
| File | Purpose |
|------|---------|
| `ZerodhaAdapter_YYYY-MM-DD.log` | Main application log |
| `Startup_YYYY-MM-DD.log` | **Critical startup events** - token validation, WebSocket connection, first tick |
| `TBS_YYYY-MM-DD.log` | TBS Manager specific events |

To change log level, create/edit: `Documents\NinjaTrader 8\ZerodhaAdapter\log4net.config`

```xml
<log4net>
  <root>
    <!-- Options: DEBUG, INFO, WARN, ERROR, FATAL -->
    <level value="INFO" />
    <appender-ref ref="RollingFileAppender" />
  </root>
</log4net>
```

**Log Levels:**
| Level | Description |
|-------|-------------|
| DEBUG | Tick-by-tick data, all callbacks, detailed diagnostics |
| INFO | Subscriptions, status changes, important events |
| WARN | Recoverable errors, deprecation warnings |
| ERROR | Failures that don't stop the system |
| FATAL | Critical system failures |

**Runtime Log Level Change:**
```csharp
Logger.SetLogLevel("DEBUG");  // Enable debug logging
Logger.SetLogLevel("INFO");   // Return to normal logging
```

### Startup Logger

The dedicated startup logger (`Startup_YYYY-MM-DD.log`) captures critical initialization events for easy diagnosis of market open failures:

**Sample Output:**
```
================================================================================
  ZERODHA ADAPTER STARTUP LOG - Session Started: 2026-01-02 10:21:24
================================================================================
  Machine: DESKTOP-24367O2
  User: Phani Krishna
  OS: Microsoft Windows NT 10.0.19045.0
  CLR: 4.0.30319.42000
================================================================================

[+5ms] ========== ADAPTER INITIALIZATION ==========
[+5ms] ZerodhaDatafeedAdapter v2.0.1 initializing...
[+257ms] [MILESTONE] Core services initialized
[+277ms] Configuration loaded successfully
[+278ms] ========== TOKEN VALIDATION ==========
[+280ms] Access token is VALID | Token validated successfully
[+2574ms] [WebSocket] Connecting | Starting WebSocket connection...
[+2578ms] [WebSocket] Token Ready | Access token validated
[+4735ms] ========== WEBSOCKET CONNECTED ==========
[+4757ms] [Subscribe] INDEX NIFTY (token=256265) - SUCCESS
[+4988ms] ========== FIRST TICK RECEIVED ==========
[+4988ms] Data flow confirmed! First tick: NIFTY2610626500CE @ 10.85
[+4988ms] Time to first tick: 4988ms from adapter start
```

**Key Events Logged:**
- Adapter initialization and version
- Configuration load success/failure
- Token validation result (critical for automated trading)
- WebSocket connection phases
- All symbol subscriptions
- **First tick received** - proof of data flow

**When to Check:**
- Market open and no data flowing → Check for "FIRST TICK RECEIVED"
- WebSocket issues → Check for "WEBSOCKET CONNECTED" or "CRITICAL ERROR"
- Token problems → Check "TOKEN VALIDATION" section

## Market Analyzer AddOn

Access via: NinjaTrader → New → Zerodha Market Analyzer

### Features
- **Indices Panel**: GIFT NIFTY, NIFTY 50, SENSEX with projected opens
- **Option Chain Tab**: Real-time CE/PE prices with straddle calculations
- **Status Indicators**: Cached, Pending, Done status per symbol

### Option Chain Display
| Strike | CE Price | CE Status | Straddle | PE Status | PE Price |
|--------|----------|-----------|----------|-----------|----------|
| 85500 | 49.40 | Done | 145.15 | Done | 95.75 |

## TBS Manager AddOn

Access via: NinjaTrader → New → TBS Manager

### Overview
Time-Based Straddle (TBS) Manager provides a simulation dashboard for monitoring short straddle strategies with multiple entry tranches throughout the trading day.

### Configuration
Create an Excel file at: `Documents\NinjaTrader 8\ZerodhaAdapter\tbsConfig.xlsx`

| Column | Description | Example |
|--------|-------------|---------|
| Underlying | Index symbol | NIFTY, BANKNIFTY, SENSEX |
| DTE | Days to expiry filter | 0, 1, 2 |
| EntryTime | Time to enter straddle | 09:20:00 |
| ExitTime | Time to exit straddle | 15:15:00 |
| IndividualSL | Per-leg stop-loss % | 50% |
| CombinedSL | Combined stop-loss % | 25% |
| HedgeAction | Action on SL hit | exit_both, hedge_to_cost |
| Quantity | Lot size | 1 |
| Active | Enable/disable row | TRUE, FALSE |

### Features

**Configuration Tab:**
- View all configurations from Excel file
- Filter by Underlying and DTE
- Refresh button to reload configurations

**Execution Tab:**
- Real-time monitoring of straddle tranches
- Status tracking: Idle → Monitoring → Live → SquaredOff
- Per-leg and combined P&L tracking
- Auto-derives underlying/DTE from Option Chain

### Status States

| Status | Description |
|--------|-------------|
| **Idle** | Entry time > 5 minutes away |
| **Monitoring** | Within 5 minutes of entry, ATM strike tracked |
| **Live** | Position entered (simulated), strike locked |
| **SquaredOff** | Position exited at exit time |
| **Skipped** | Entry time passed while not monitoring |

### Integration with Option Chain
- Waits 45 seconds after Option Chain loads before initializing
- Gets ATM strike from Option Chain via `MarketAnalyzerLogic.GetATMStrike()`
- Gets real-time prices from PriceHub (no duplicate subscriptions)
- Auto-filters configs based on Option Chain's underlying and DTE

## Synthetic Straddle Instruments

Create straddle instruments that combine CE + PE into a single tradeable symbol:

**Symbol Format:** `{UNDERLYING}{EXPIRY}{STRIKE}_STRDL`

**Example:** `SENSEX25DEC85500_STRDL`

These instruments:
- Display combined CE+PE price as the "Last" price
- Support historical bars (synthetic from leg data)
- Auto-subscribe to both legs

## Troubleshooting

### Quick Diagnosis with Startup Log

**First step for any startup issue**: Check `Startup_YYYY-MM-DD.log` in `Documents\NinjaTrader 8\ZerodhaAdapter\Logs\`

| Symptom | What to Look For |
|---------|------------------|
| No data at market open | "FIRST TICK RECEIVED" section missing |
| WebSocket not connecting | "WEBSOCKET CONNECTED" missing or "CRITICAL ERROR" present |
| Token issues | "TOKEN VALIDATION" shows failure |
| Subscriptions failing | "[Subscribe]" entries show QUEUED instead of SUCCESS |

### Common Issues

**No market data at market open:**
1. Check `Startup_YYYY-MM-DD.log` for "FIRST TICK RECEIVED"
2. If missing, check for WebSocket or token errors above it
3. Token validation must show "VALID" for data to flow

**UI stops updating after "Done" status:**
- Fixed in v1.x: Multi-callback support ensures SubscriptionManager callback persists

**Chart not receiving ticks:**
- Verify subscription with `[ZerodhaAdapter] SubscribeMarketData: ADDED callback` logs
- Check callback count: should be 2+ for symbols with multiple consumers

**WebSocket disconnects:**
- Check `[SharedWS]` logs for reconnection attempts
- Check Startup log for "CRITICAL ERROR" entries
- Verify internet connectivity and Zerodha API status

**Token validation failures:**
- Check Startup log "TOKEN VALIDATION" section
- Verify Zerodha credentials are valid
- Token may need manual refresh via Zerodha login

### Debug Logging

Enable DEBUG level to see:
- `[OTP] ProcessSingleTick` - Every tick processed
- `[OTP-RX] Publishing to reactive stream` - Rx.NET stream publishing
- `[SubscriptionManager] Pipeline Stage 1/2/3` - TPL Dataflow pipeline processing
- `[SubscriptionManager] LiveData CALLBACK FIRING` - Callback invocations
- `[OptionChainTabPage] OnOptionPriceUpdated RECEIVED` - UI updates
- `[L1Subscription] AddCallback` - Callback registrations
- `[MarketAnalyzerLogic] ATM strike updated` - ATM changes from Option Chain
- `[TBSManagerTabPage] Locked strike` - TBS Manager strike locks
- `[SharedWS] State: X -> Y` - WebSocket state transitions
- `[SharedWS] Backing off for Xms` - Exponential backoff in progress
- `[SharedWS] Backoff state reset` - Successful reconnection

## Version History

See [CHANGES.MD](CHANGES.MD) for detailed changelog.

## License

Proprietary - For authorized use only.
