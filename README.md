# Zerodha NinjaAdapter

A high-performance adapter for connecting NinjaTrader 8 to Indian trading platforms, primarily Zerodha, with support for real-time market data, synthetic straddles, and option chain analysis.

## Overview

Zerodha NinjaAdapter is a comprehensive bridge solution that enables NinjaTrader 8 to seamlessly connect with Zerodha and other Indian brokers. The adapter provides real-time market data integration, historical data access, synthetic straddle instruments, and a Market Analyzer for option chain visualization.

## Key Features

- **Shared WebSocket Architecture**: Single WebSocket connection for all symbols (up to 1000 per connection)
- **High-Performance Tick Processing**: Disruptor-style sharded RingBuffer with 4 parallel worker threads
- **Synthetic Straddle Instruments**: Real-time CE+PE combined pricing for options trading
- **Market Analyzer Window**: Visual option chain with ATM detection and histogram display
- **Multi-Broker Support**: Zerodha, Upstox, and Binance connectivity
- **Automatic Token Generation**: TOTP-based auto-login with token refresh
- **Memory-Optimized**: ArrayPool integration, GC pressure monitoring, and intelligent backpressure

## Architecture

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                              NinjaTrader 8                                  │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐  ┌───────────────────┐   │
│  │   Charts    │  │Market Depth │  │ Control Ctr │  │ Market Analyzer   │   │
│  └──────┬──────┘  └──────┬──────┘  └──────┬──────┘  │ (Option Chain)    │   │
│         │                │                │         └─────────┬─────────┘   │
└─────────┼────────────────┼────────────────┼───────────────────┼─────────────┘
          │                │                │                   │
          ▼                ▼                ▼                   ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                           ZerodhaDatafeedAdapter                                    │
│                                                                             │
│  ┌─────────────────────────────────────────────────────────────────────┐    │
│  │                         ZerodhaAdapter (Main Entry)                      │    │
│  │  • SubscribeMarketData()  • SubscribeMarketDepth()                  │    │
│  │  • ProcessSyntheticLegTick()  • Historical Data Requests            │    │
│  └──────────────────────────────────┬──────────────────────────────────┘    │
│                                     │                                       │
│  ┌──────────────────────────────────┼──────────────────────────────────┐    │
│  │                          Connector                                  │    │
│  │  Orchestrates all services and manages adapter lifecycle            │    │
│  └──────────────────────────────────┬──────────────────────────────────┘    │
│                                     │                                       │
│  ┌──────────────────────────────────┴──────────────────────────────────┐    │
│  │                                                                     │    │
│  │  ┌─────────────────────┐    ┌─────────────────────────────────┐     │    │
│  │  │  MarketDataService  │    │   SharedWebSocketService        │     │    │
│  │  │  • SubscribeToTicks │◄──►│   • Single WS connection        │     │    │
│  │  │  • SubscribeToDepth │    │   • Multi-symbol subscription   │     │    │
│  │  │  • Feature Flags    │    │   • Token-to-symbol routing     │     │    │
│  │  └─────────┬───────────┘    │   • Auto-reconnect              │     │    │
│  │            │                └─────────────────────────────────┘     │    │
│  │            ▼                                                        │    │
│  │  ┌─────────────────────────────────────────────────────────────┐    │    │
│  │  │              OptimizedTickProcessor                         │    │    │
│  │  │  ┌─────────────────────────────────────────────────────┐    │    │    │
│  │  │  │            Sharded RingBuffer (4 Shards)            │    │    │    │
│  │  │  │  ┌─────────┐ ┌─────────┐ ┌─────────┐ ┌─────────┐    │    │    │    │
│  │  │  │  │Shard 0  │ │Shard 1  │ │Shard 2  │ │Shard 3  │    │    │    │    │
│  │  │  │  │16K items│ │16K items│ │16K items│ │16K items│    │    │    │    │
│  │  │  │  └────┬────┘ └────┬────┘ └────┬────┘ └────┬────┘    │    │    │    │
│  │  │  │       │           │           │           │         │    │    │    │
│  │  │  │       ▼           ▼           ▼           ▼         │    │    │    │
│  │  │  │  ┌─────────┐ ┌─────────┐ ┌─────────┐ ┌─────────┐    │    │    │    │
│  │  │  │  │Worker 0 │ │Worker 1 │ │Worker 2 │ │Worker 3 │    │    │    │    │
│  │  │  │  └─────────┘ └─────────┘ └─────────┘ └─────────┘    │    │    │    │
│  │  │  └─────────────────────────────────────────────────────┘    │    │    │
│  │  │  • Tiered Backpressure (60%/80%/90% thresholds)             │    │    │
│  │  │  • Memory Pressure Detection & GC Monitoring                │    │    │
│  │  │  • O(1) Callback Caching                                    │    │    │
│  │  │  • Race Condition Protection (instrument init checks)       │    │    │
│  │  └─────────────────────────────────────────────────────────────┘    │    │
│  │                                                                     │    │
│  │  ┌─────────────────────┐    ┌─────────────────────────────────┐     │    │
│  │  │ SyntheticStraddle   │    │   MarketAnalyzerLogic           │     │    │
│  │  │ Service             │    │   • GIFT NIFTY projected open   │     │    │
│  │  │ • CE+PE combining   │    │   • Option chain generation     │     │    │
│  │  │ • Straddle pricing  │    │   • ATM strike detection        │     │    │
│  │  └─────────────────────┘    └─────────────────────────────────┘     │    │
│  │                                                                     │    │
│  │  ┌─────────────────────┐    ┌─────────────────────────────────┐     │    │
│  │  │ InstrumentManager   │    │   HistoricalDataService         │     │    │
│  │  │ • Symbol mapping    │    │   • Zerodha historical API      │     │    │
│  │  │ • Token lookup      │    │   • 1min/5min/daily bars        │     │    │
│  │  │ • NT instrument     │    │   • Gap fill support            │     │    │
│  │  │   creation          │    └─────────────────────────────────┘     │    │
│  │  └─────────────────────┘                                            │    │
│  │                                                                     │    │
│  └─────────────────────────────────────────────────────────────────────┘    │
│                                                                             │
│  ┌─────────────────────────────────────────────────────────────────────┐    │
│  │                        AddOns / UI                                  │    │
│  │  ┌─────────────────────┐    ┌─────────────────────────────────┐     │    │
│  │  │ MarketAnalyzerWindow│    │   OptionChainWindow             │     │    │
│  │  │ • Ticker display    │    │   • Strike-wise CE/PE prices    │     │    │
│  │  │ • Projected open    │    │   • Straddle column             │     │    │
│  │  │ • Status indicators │    │   • ATM highlighting            │     │    │
│  │  └─────────────────────┘    │   • Histogram visualization     │     │    │
│  │                             │   • NTTabPage instrument linking │     │    │
│  │                             └─────────────────────────────────┘     │    │
│  └─────────────────────────────────────────────────────────────────────┘    │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
                                     │
                                     ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                              ZerodhaAPI                                    │
│                                                                             │
│  ┌─────────────────────────────────────────────────────────────────────┐    │
│  │                            Common                                   │    │
│  │  ┌────────────┐  ┌────────────┐  ┌────────────┐  ┌────────────┐     │    │
│  │  │   Models   │  │   Enums    │  │ Interfaces │  │ Extensions │     │    │
│  │  └────────────┘  └────────────┘  └────────────┘  └────────────┘     │    │
│  └─────────────────────────────────────────────────────────────────────┘    │
│                                                                             │
│  ┌───────────────────────────┐   ┌───────────────┐   ┌───────────────┐      │
│  │         Zerodha           │   │    Upstox     │   │    Binance    │      │
│  │  ┌─────────────────────┐  │   │               │   │               │      │
│  │  │   WebSocket Binary  │  │   │  REST Client  │   │  WebSocket    │      │
│  │  │   Parser (Manual    │  │   │               │   │               │      │
│  │  │   Big-Endian)       │  │   │               │   │               │      │
│  │  ├─────────────────────┤  │   │               │   │               │      │
│  │  │   REST Client       │  │   │               │   │               │      │
│  │  │   (Kite Connect)    │  │   │               │   │               │      │
│  │  ├─────────────────────┤  │   │               │   │               │      │
│  │  │   TOTP Generator    │  │   │               │   │               │      │
│  │  │   (Auto-Login)      │  │   │               │   │               │      │
│  │  └─────────────────────┘  │   │               │   │               │      │
│  └───────────────────────────┘   └───────────────┘   └───────────────┘      │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
                │                        │                     │
                ▼                        ▼                     ▼
       ┌─────────────────┐      ┌─────────────────┐    ┌─────────────────┐
       │  Zerodha Kite   │      │   Upstox API    │    │   Binance API   │
       │  Connect API    │      │                 │    │                 │
       │  wss://ws.kite  │      │                 │    │                 │
       └─────────────────┘      └─────────────────┘    └─────────────────┘
```

## WebSocket Architecture

### Shared WebSocket (New - Recommended)

The adapter now uses a **single shared WebSocket connection** for all symbol subscriptions:

```
┌──────────────────────────────────────────────────────────────────────┐
│                    SharedWebSocketService                            │
│                                                                      │
│  ┌────────────────────────────────────────────────────────────────┐  │
│  │                 Single WebSocket Connection                    │  │
│  │                 wss://ws.kite.zerodha.com                      │  │
│  └────────────────────────────────────────────────────────────────┘  │
│                              │                                       │
│                              ▼                                       │
│  ┌────────────────────────────────────────────────────────────────┐  │
│  │              Subscription Manager                              │  │
│  │  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐             │  │
│  │  │ GIFT_NIFTY  │  │ NIFTY25DEC  │  │ SENSEX25DEC │  ... (100+) │  │
│  │  │ Token:291849│  │ 24000CE     │  │ 85000PE     │             │  │
│  │  └─────────────┘  └─────────────┘  └─────────────┘             │  │
│  └────────────────────────────────────────────────────────────────┘  │
│                              │                                       │
│                              ▼                                       │
│  ┌────────────────────────────────────────────────────────────────┐  │
│  │              Token-to-Symbol Router                            │  │
│  │  291849 → GIFT_NIFTY   |   12345 → NIFTY25DEC24000CE          │  │
│  │  Incoming tick → Find symbol → Fire TickReceived event         │  │
│  └────────────────────────────────────────────────────────────────┘  │
│                                                                      │
└──────────────────────────────────────────────────────────────────────┘
```

**Benefits:**
- Uses 1 of Zerodha's 3 allowed connections (instead of 100+)
- Batch subscription messages reduce API overhead
- Centralized reconnection and error handling
- Automatic pending queue for symbols added before connection ready

### Legacy WebSocket (Deprecated)

The old architecture created one WebSocket per symbol, which:
- Exceeded Zerodha's 3-connection limit
- Caused 403 Forbidden errors
- Wasted resources on connection overhead

Set `MarketDataService.UseSharedWebSocket = false` to revert (not recommended).

## Tick Processing Pipeline

```
WebSocket Binary Message
         │
         ▼
┌────────────────────────┐
│   Parse Binary Packet  │  ← Manual big-endian parsing (no System.Buffers.Binary)
│   Extract: token, LTP, │
│   OHLC, volume, depth  │
└───────────┬────────────┘
            │
            ▼
┌────────────────────────┐
│  Token → Symbol Lookup │
│  Route to correct      │
│  subscription handler  │
└───────────┬────────────┘
            │
            ▼
┌────────────────────────────────────────────────────────────────┐
│              OptimizedTickProcessor                            │
│                                                                │
│  QueueTick(symbol, tickData)                                   │
│         │                                                      │
│         ▼                                                      │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │  Shard Selection: hash(symbol) % 4                       │  │
│  │  Ensures same symbol always goes to same shard           │  │
│  └────────────────────────┬─────────────────────────────────┘  │
│                           │                                    │
│         ┌─────────────────┼─────────────────┐                  │
│         ▼                 ▼                 ▼                  │
│  ┌───────────┐     ┌───────────┐     ┌───────────┐             │
│  │ RingBuffer│     │ RingBuffer│     │ RingBuffer│   (x4)      │
│  │  16K pre- │     │  16K pre- │     │  16K pre- │             │
│  │ allocated │     │ allocated │     │ allocated │             │
│  │   items   │     │   items   │     │   items   │             │
│  └─────┬─────┘     └─────┬─────┘     └─────┬─────┘             │
│        │                 │                 │                   │
│        ▼                 ▼                 ▼                   │
│  ┌───────────┐     ┌───────────┐     ┌───────────┐             │
│  │  Worker   │     │  Worker   │     │  Worker   │   (x4)      │
│  │  Thread   │     │  Thread   │     │  Thread   │             │
│  │ (Dedicated│     │ (Dedicated│     │ (Dedicated│             │
│  │ LongRun)  │     │ LongRun)  │     │ LongRun)  │             │
│  └─────┬─────┘     └─────┬─────┘     └─────┬─────┘             │
│        │                 │                 │                   │
│        └─────────────────┼─────────────────┘                   │
│                          │                                     │
│                          ▼                                     │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │  ProcessCallbacks()                                      │  │
│  │  • Null-safe instrument checks (race condition fix)      │  │
│  │  • Round to tick size                                    │  │
│  │  • Fire NinjaTrader callbacks                            │  │
│  │  • Track callback timing (slow callback detection)       │  │
│  └──────────────────────────────────────────────────────────┘  │
│                                                                │
│  Backpressure Management:                                      │
│  • Warning: 60% queue full → log warning                       │
│  • Critical: 80% → start dropping old ticks                    │
│  • Emergency: 90% → only essential symbols (NIFTY, SENSEX)     │
│  • Maximum: 100% → reject all new ticks                        │
│                                                                │
└────────────────────────────────────────────────────────────────┘
```

## Installation

1. Build the solution in Visual Studio (Debug or Release)
2. Copy `ZerodhaDatafeedAdapter.dll` to:
   ```
   %UserProfile%\Documents\NinjaTrader 8\bin\Custom
   ```
3. Create the configuration folder:
   ```
   %UserProfile%\Documents\NinjaTrader 8\ZerodhaAdapter
   ```
4. Create `config.json` with your Zerodha credentials
5. Restart NinjaTrader 8
6. Add "Zerodha" connection in Control Center → Connections

## Configuration

### config.json

```json
{
  "Active": {
    "Websocket": "Zerodha",
    "Historical": "Zerodha"
  },
  "Zerodha": {
    "Api": "your_api_key",
    "Secret": "your_secret_key",
    "AccessToken": "",
    "AccessTokenExpiry": "",
    "UserId": "your_user_id",
    "Password": "your_password",
    "TotpSecret": "your_totp_secret",
    "RedirectUrl": "http://127.0.0.1:8001/callback",
    "AutoLogin": true
  },
  "GeneralSettings": {
    "EnableVerboseTickLogging": false,
    "AutoGenerateToken": true,
    "TokenRefreshBeforeExpiryMinutes": 30
  }
}
```

### Instrument Mappings

The adapter uses JSON files for symbol mapping:

- `mapped_instruments.json` - Zerodha symbol to NinjaTrader mapping
- `index_mappings.json` - Index symbols (GIFT NIFTY, NIFTY 50, SENSEX)
- `straddles_config.json` - Auto-generated synthetic straddle definitions

## Market Analyzer

The Market Analyzer provides real-time visualization of:

1. **Ticker Panel**: GIFT NIFTY, NIFTY, SENSEX with live prices and % change
2. **Projected Open**: Calculated from GIFT NIFTY correlation
3. **Option Chain**:
   - Strike-wise CE/PE prices
   - Straddle prices (CE + PE)
   - ATM strike highlighting
   - Premium histograms

Access via: NinjaTrader → New → Market Analyzer (Zerodha)

## Synthetic Straddles

The adapter supports synthetic straddle instruments that combine CE and PE legs:

```
Symbol Format: NIFTY25DEC24000_STRDL
Components:   NIFTY25DEC24000CE + NIFTY25DEC24000PE
Price:        Sum of CE and PE last traded prices
```

These appear as regular instruments in NinjaTrader and receive real-time price updates.

## Logging

Logs are written to:
```
%UserProfile%\Documents\NinjaTrader 8\ZerodhaAdapter\Logs\ZerodhaAdapter_YYYY-MM-DD.log
```

Key log prefixes:
- `[SharedWS]` - Shared WebSocket connection events
- `[TICK-SHARED]` - Subscription via shared WebSocket
- `[OTP]` - OptimizedTickProcessor events
- `[MarketAnalyzerLogic]` - Option chain and projected open calculations

## Troubleshooting

### No ticks received
1. Check if WebSocket is connected: Look for `[SharedWS] Connected successfully`
2. Verify access token is valid (not expired)
3. Check symbol mapping exists in `mapped_instruments.json`

### 403 Forbidden errors
- Usually indicates expired access token
- Enable `AutoLogin: true` in config.json for automatic token refresh

### "Object reference not set" errors
- Fixed in latest version with race condition protection
- Rebuild and redeploy the DLL

### High memory usage
- The adapter monitors GC pressure automatically
- Logs will show `MEMORY PRESSURE DETECTED` if issues occur
- Caches are automatically trimmed under pressure

## Development

### Prerequisites
- Visual Studio 2022
- .NET Framework 4.8
- NinjaTrader 8 installed

### Build
```bash
MSBuild ZerodhaDatafeedAdapter.csproj /t:Build /p:Configuration=Debug
```

### Deploy
```bash
copy /Y "bin\Debug\ZerodhaDatafeedAdapter.dll" "%UserProfile%\Documents\NinjaTrader 8\bin\Custom\"
```

## License

Proprietary software. All rights reserved.

## Acknowledgements

- NinjaTrader developer community
- Zerodha Kite Connect API
- LMAX Disruptor pattern (inspiration for RingBuffer design)
