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

### Performance Optimizations
- **OptimizedTickProcessor**: Lock-free concurrent tick processing with sharded parallelism
- **SharedWebSocketService**: Single WebSocket connection shared across all subscriptions
- **Multi-Callback Support**: Multiple subscribers (Chart, Option Chain, Market Analyzer) can receive the same tick data
- **Intelligent Throttling**: UI updates throttled to prevent overwhelming the display

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
| **L1Subscription** | Thread-safe multi-callback container - allows multiple consumers per symbol |
| **OptimizedTickProcessor** | High-performance tick processing with sharded parallelism |
| **SharedWebSocketService** | Single shared WebSocket connection for all market data |
| **SubscriptionManager** | Manages option chain subscriptions and BarsRequests |
| **MarketAnalyzerLogic** | Calculates projected opens and generates option chains |
| **OptionChainWindow** | WPF UI for displaying real-time option chain data |
| **SubscriptionTrackingService** | Reference counting and sticky subscriptions |

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

## Synthetic Straddle Instruments

Create straddle instruments that combine CE + PE into a single tradeable symbol:

**Symbol Format:** `{UNDERLYING}{EXPIRY}{STRIKE}_STRDL`

**Example:** `SENSEX25DEC85500_STRDL`

These instruments:
- Display combined CE+PE price as the "Last" price
- Support historical bars (synthetic from leg data)
- Auto-subscribe to both legs

## Troubleshooting

### Common Issues

**UI stops updating after "Done" status:**
- Fixed in v1.x: Multi-callback support ensures SubscriptionManager callback persists

**Chart not receiving ticks:**
- Verify subscription with `[ZerodhaAdapter] SubscribeMarketData: ADDED callback` logs
- Check callback count: should be 2+ for symbols with multiple consumers

**WebSocket disconnects:**
- Check `[SharedWS]` logs for reconnection attempts
- Verify internet connectivity and Zerodha API status

### Debug Logging

Enable DEBUG level to see:
- `[OTP] ProcessSingleTick` - Every tick processed
- `[SubscriptionManager] LiveData CALLBACK FIRING` - Callback invocations
- `[OptionChainTabPage] OnOptionPriceUpdated RECEIVED` - UI updates
- `[L1Subscription] AddCallback` - Callback registrations

## Version History

See [CHANGES.MD](CHANGES.MD) for detailed changelog.

## License

Proprietary - For authorized use only.
