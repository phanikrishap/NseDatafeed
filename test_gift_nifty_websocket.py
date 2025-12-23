"""
Test script to verify Zerodha WebSocket stream for GIFT NIFTY
Uses KiteTicker to subscribe to real-time tick data
"""

import logging
from kiteconnect import KiteTicker

# Configure logging
logging.basicConfig(
    level=logging.DEBUG,
    format='%(asctime)s - %(levelname)s - %(message)s'
)

# Zerodha credentials from config.json
API_KEY = "6g794lmwuo1wdmr7"
ACCESS_TOKEN = "mhP4vlFisyWhgWlV6KHvjc4ff1nvHtxK"

# GIFT NIFTY instrument token from index_mappings.json
GIFT_NIFTY_TOKEN = 291849

def on_ticks(ws, ticks):
    """Callback when tick data is received"""
    for tick in ticks:
        print("\n" + "="*60)
        print(f"GIFT NIFTY TICK DATA")
        print("="*60)
        print(f"  Instrument Token: {tick.get('instrument_token')}")
        print(f"  Last Price:       {tick.get('last_price')}")
        print(f"  Last Quantity:    {tick.get('last_quantity')}")
        print(f"  Volume:           {tick.get('volume')}")
        print(f"  Buy Quantity:     {tick.get('buy_quantity')}")
        print(f"  Sell Quantity:    {tick.get('sell_quantity')}")
        print(f"  Open:             {tick.get('ohlc', {}).get('open')}")
        print(f"  High:             {tick.get('ohlc', {}).get('high')}")
        print(f"  Low:              {tick.get('ohlc', {}).get('low')}")
        print(f"  Close:            {tick.get('ohlc', {}).get('close')}")
        print(f"  Change:           {tick.get('change')}")
        print(f"  Timestamp:        {tick.get('timestamp')}")
        print(f"  Last Trade Time:  {tick.get('last_trade_time')}")
        print("="*60)

def on_connect(ws, response):
    """Callback on successful connection"""
    print("\n*** CONNECTED TO ZERODHA WEBSOCKET ***")
    print(f"Response: {response}")

    # Subscribe to GIFT NIFTY with QUOTE mode (includes volume for futures)
    # MODE_LTP = 1 (only last price)
    # MODE_QUOTE = 2 (quote data with volume)
    # MODE_FULL = 3 (full data - but for indices doesn't include volume)
    print(f"\nSubscribing to GIFT NIFTY (token: {GIFT_NIFTY_TOKEN}) in QUOTE mode...")
    ws.subscribe([GIFT_NIFTY_TOKEN])
    ws.set_mode(ws.MODE_QUOTE, [GIFT_NIFTY_TOKEN])
    print("Subscription request sent (MODE_QUOTE). Waiting for ticks...\n")

def on_close(ws, code, reason):
    """Callback when connection is closed"""
    print(f"\n*** CONNECTION CLOSED ***")
    print(f"Code: {code}, Reason: {reason}")

def on_error(ws, code, reason):
    """Callback when error occurs"""
    print(f"\n*** ERROR ***")
    print(f"Code: {code}, Reason: {reason}")

def on_reconnect(ws, attempts_count):
    """Callback when reconnecting"""
    print(f"\n*** RECONNECTING (attempt {attempts_count}) ***")

def on_noreconnect(ws):
    """Callback when max reconnect attempts exceeded"""
    print("\n*** MAX RECONNECT ATTEMPTS EXCEEDED ***")

def on_order_update(ws, data):
    """Callback for order updates"""
    print(f"\n*** ORDER UPDATE: {data} ***")

def main():
    print("="*60)
    print("GIFT NIFTY WebSocket Test Script")
    print("="*60)
    print(f"API Key:      {API_KEY}")
    print(f"Access Token: {ACCESS_TOKEN[:10]}...")
    print(f"Token:        {GIFT_NIFTY_TOKEN}")
    print("="*60)
    print("\nInitializing KiteTicker...")

    # Create KiteTicker instance
    kws = KiteTicker(API_KEY, ACCESS_TOKEN)

    # Assign callbacks
    kws.on_ticks = on_ticks
    kws.on_connect = on_connect
    kws.on_close = on_close
    kws.on_error = on_error
    kws.on_reconnect = on_reconnect
    kws.on_noreconnect = on_noreconnect
    kws.on_order_update = on_order_update

    print("Connecting to Zerodha WebSocket...")
    print("Press Ctrl+C to stop\n")

    # Connect (blocking call)
    kws.connect()

if __name__ == "__main__":
    main()
