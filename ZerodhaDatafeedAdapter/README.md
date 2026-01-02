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

### Reliability & Diagnostics
- **TaskCompletionSource Pattern**: Robust async signaling for token/connection ready states - late subscribers never miss events
- **Event-Driven Token Validation**: WebSocket waits for valid token before connecting
- **State Machine Transitions**: Proper state management in SharedWebSocketService with safe transitions
- **Dedicated Startup Logger**: Critical startup events logged to separate file for easy diagnosis

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
│  │  │                 │  │ Service          │  │ (Options/Straddles)     │ │  │
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
| **ZerodhaAdapter** | NinjaTrader adapter interface - handles Subscribe/Unsubscribe calls |
| **Connector** | Service orchestration, token validation, singleton access hub with TaskCompletionSource pattern |
| **L1Subscription** | Thread-safe multi-callback container - allows multiple consumers per symbol |
| **OptimizedTickProcessor** | High-performance tick processing with sharded parallelism and tiered backpressure |
| **SharedWebSocketService** | Single shared WebSocket connection with state machine and TCS-based connection signaling |
| **SubscriptionManager** | Manages option chain subscriptions and BarsRequests |
| **MarketAnalyzerLogic** | Calculates projected opens, generates option chains, hosts PriceHub and ATM tracking |
| **OptionChainWindow** | WPF UI for displaying real-time option chain data |
| **TBSManagerWindow** | TBS Manager addon for time-based straddle execution simulation |
| **TBSConfigurationService** | Reads straddle configurations from Excel file |
| **SubscriptionTrackingService** | Reference counting and sticky subscriptions |
| **InstrumentManager** | Handles symbol mapping, token lookup, and NT instrument creation |
| **StartupLogger** | Dedicated logger for critical startup events - token validation, WebSocket, first tick |
| **TBSLogger** | Dedicated logger for TBS Manager events |

### Data Flow

1. **WebSocket Tick** → `SharedWebSocketService` receives binary tick data
2. **Parse** → Tick data parsed into `ZerodhaTickData` object
3. **Queue** → Tick queued in `OptimizedTickProcessor`
4. **Process** → Sharded workers process ticks in parallel
5. **Callbacks** → ALL registered callbacks invoked (Chart, Option Chain, etc.)
6. **NinjaTrader** → Data fed to NinjaTrader's market data system

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
- `[SubscriptionManager] LiveData CALLBACK FIRING` - Callback invocations
- `[OptionChainTabPage] OnOptionPriceUpdated RECEIVED` - UI updates
- `[L1Subscription] AddCallback` - Callback registrations
- `[MarketAnalyzerLogic] ATM strike updated` - ATM changes from Option Chain
- `[TBSManagerTabPage] Locked strike` - TBS Manager strike locks

## Version History

See [CHANGES.MD](CHANGES.MD) for detailed changelog.

## License

Proprietary - For authorized use only.
