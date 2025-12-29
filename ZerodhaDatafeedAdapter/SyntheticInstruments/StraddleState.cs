using System;
using System.Collections.Generic;
using System.Linq;

namespace ZerodhaDatafeedAdapter.SyntheticInstruments
{
    /// <summary>
    /// Holds the dynamic, real-time data for each active synthetic straddle.
    /// It specifically tracks the last known price for each leg.
    /// </summary>
    public class StraddleState
    {
        /// <summary>
        /// The static definition of this straddle.
        /// </summary>
        public StraddleDefinition Definition { get; }

        /// <summary>
        /// The last known price of the Call option leg.
        /// </summary>
        public double LastCEPrice { get; set; }

        /// <summary>
        /// The last known price of the Put option leg.
        /// </summary>
        public double LastPEPrice { get; set; }

        /// <summary>
        /// The last known bid price of the Call option leg.
        /// </summary>
        public double LastCEBidPrice { get; set; }

        /// <summary>
        /// The last known ask price of the Call option leg.
        /// </summary>
        public double LastCEAskPrice { get; set; }

        /// <summary>
        /// The last known bid price of the Put option leg.
        /// </summary>
        public double LastPEBidPrice { get; set; }

        /// <summary>
        /// The last known ask price of the Put option leg.
        /// </summary>
        public double LastPEAskPrice { get; set; }

        /// <summary>
        /// Timestamp of the last received tick for the Call leg.
        /// </summary>
        public DateTime LastCETimestamp { get; set; }

        /// <summary>
        /// Timestamp of the last received tick for the Put leg.
        /// </summary>
        public DateTime LastPETimestamp { get; set; }
        
        /// <summary>
        /// The last known volume of the Call option leg.
        /// </summary>
        public long LastCEVolume { get; set; }
        
        /// <summary>
        /// The last known volume of the Put option leg.
        /// </summary>
        public long LastPEVolume { get; set; }
        
        /// <summary>
        /// Flag indicating whether the CE leg's volume has been incorporated into a synthetic tick.
        /// </summary>
        public bool CEVolumeIncorporated { get; set; }
        
        /// <summary>
        /// Flag indicating whether the PE leg's volume has been incorporated into a synthetic tick.
        /// </summary>
        public bool PEVolumeIncorporated { get; set; }
        
        /// <summary>
        /// List of recent ticks for the CE leg (up to 5).
        /// </summary>
        public List<Tick> RecentCETicks { get; private set; }
        
        /// <summary>
        /// List of recent ticks for the PE leg (up to 5).
        /// </summary>
        public List<Tick> RecentPETicks { get; private set; }

        /// <summary>
        /// Indicates if at least one tick has been received for the Call leg.
        /// </summary>
        public bool HasCEData { get; set; }

        /// <summary>
        /// Indicates if at least one tick has been received for the Put leg.
        /// </summary>
        public bool HasPEData { get; set; }
        
        /// <summary>
        /// Indicates if at least one bid/ask tick has been received for the Call leg.
        /// </summary>
        public bool HasCEBidAskData { get; set; }
        
        /// <summary>
        /// Indicates if at least one bid/ask tick has been received for the Put leg.
        /// </summary>
        public bool HasPEBidAskData { get; set; }
        
        /// <summary>
        /// The last volume we injected for the synthetic instrument.
        /// </summary>
        public long LastSyntheticVolume { get; set; }
        
        /// <summary>
        /// The cumulative volume for the synthetic instrument for the current session.
        /// </summary>
        public long CumulativeSyntheticVolume { get; set; }
        
        /// <summary>
        /// Queue of unincorporated CE volumes waiting to be processed
        /// </summary>
        public Queue<VolumeQueueItem> CEVolumeQueue { get; private set; }
        
        /// <summary>
        /// Queue of unincorporated PE volumes waiting to be processed
        /// </summary>
        public Queue<VolumeQueueItem> PEVolumeQueue { get; private set; }
        
        /// <summary>
        /// Maximum age (in milliseconds) for volume items before they're considered aged out
        /// </summary>
        public const int VOLUME_MAX_AGE_MS = 500; // 500ms max age
        
        /// <summary>
        /// Maximum queue size to prevent unbounded growth
        /// </summary>
        public const int MAX_VOLUME_QUEUE_SIZE = 10;

        /// <summary>
        /// Creates a new instance of the StraddleState class.
        /// </summary>
        /// <param name="definition">The straddle definition.</param>
        public StraddleState(StraddleDefinition definition)
        {
            Definition = definition ?? throw new ArgumentNullException(nameof(definition));
            LastCEPrice = 0.0;
            LastPEPrice = 0.0;
            LastCETimestamp = DateTime.MinValue;
            LastPETimestamp = DateTime.MinValue;
            LastCEVolume = 0;
            LastPEVolume = 0;
            HasCEData = false;
            HasPEData = false;
            HasCEBidAskData = false;
            HasPEBidAskData = false;
            LastSyntheticVolume = 0;
            CumulativeSyntheticVolume = 0;
            RecentCETicks = new List<Tick>(5);
            RecentPETicks = new List<Tick>(5);
            CEVolumeQueue = new Queue<VolumeQueueItem>();
            PEVolumeQueue = new Queue<VolumeQueueItem>();
        }

        /// <summary>
        /// Gets the current synthetic straddle price (sum of last known leg prices).
        /// Returns 0.0 if both legs haven't reported data yet.
        /// </summary>
        public double GetSyntheticPrice()
        {
            if (HasCEData && HasPEData)
            {
                return LastCEPrice + LastPEPrice;
            }
            return 0.0;
        }

        /// <summary>
        /// Gets the current synthetic straddle bid price (sum of CE and PE bid prices).
        /// Returns 0.0 if both legs haven't reported bid/ask data yet.
        /// </summary>
        public double GetSyntheticBidPrice()
        {
            if (HasCEBidAskData && HasPEBidAskData)
            {
                return LastCEBidPrice + LastPEBidPrice;
            }
            return 0.0;
        }

        /// <summary>
        /// Gets the current synthetic straddle ask price (sum of CE and PE ask prices).
        /// Returns 0.0 if both legs haven't reported bid/ask data yet.
        /// </summary>
        public double GetSyntheticAskPrice()
        {
            if (HasCEBidAskData && HasPEBidAskData && LastCEAskPrice > 0 && LastPEAskPrice > 0)
            {
                return LastCEAskPrice + LastPEAskPrice;
            }
            return 0.0;
        }

        /// <summary>
        /// Limitations the recent ticks to a certain count.
        /// </summary>
        public void LimitRecentTicks(int maxCount = 2)
        {
            while (RecentCETicks.Count > maxCount)
            {
                RecentCETicks.RemoveAt(0);
            }
            
            while (RecentPETicks.Count > maxCount)
            {
                RecentPETicks.RemoveAt(0);
            }
        }

        /// <summary>
        /// Gets unincorporated volumes that haven't been used in synthetic ticks yet
        /// </summary>
        public long GetPendingCEVolume()
        {
            return CEVolumeQueue.Sum(item => item.Volume);
        }
        
        /// <summary>
        /// Gets unincorporated volumes that haven't been used in synthetic ticks yet
        /// </summary>
        public long GetPendingPEVolume()
        {
            return PEVolumeQueue.Sum(item => item.Volume);
        }
        
        /// <summary>
        /// Cleans up aged volume items from queues
        /// </summary>
        public int CleanupAgedVolumes()
        {
            int removedCount = 0;
            var cutoffTime = DateTime.UtcNow.AddMilliseconds(-VOLUME_MAX_AGE_MS);
            
            while (CEVolumeQueue.Count > 0 && CEVolumeQueue.Peek().Timestamp < cutoffTime)
            {
                CEVolumeQueue.Dequeue();
                removedCount++;
            }
            
            while (PEVolumeQueue.Count > 0 && PEVolumeQueue.Peek().Timestamp < cutoffTime)
            {
                PEVolumeQueue.Dequeue();
                removedCount++;
            }
            
            return removedCount;
        }
        
        /// <summary>
        /// Adds volume to the appropriate queue
        /// </summary>
        public void QueueVolume(bool isCELeg, long volume, DateTime timestamp)
        {
            var queue = isCELeg ? CEVolumeQueue : PEVolumeQueue;
            
            queue.Enqueue(new VolumeQueueItem
            {
                Volume = volume,
                Timestamp = timestamp
            });
            
            while (queue.Count > MAX_VOLUME_QUEUE_SIZE)
            {
                queue.Dequeue();
            }
        }
        
        /// <summary>
        /// Attempts to match and consume volumes from both queues
        /// </summary>
        public (long ceVolume, long peVolume, bool foundMatch) TryMatchVolumes(DateTime currentTimestamp)
        {
            CleanupAgedVolumes();
            
            if (CEVolumeQueue.Count == 0 || PEVolumeQueue.Count == 0)
            {
                var ceVol = CEVolumeQueue.Count > 0 ? CEVolumeQueue.Peek().Volume : 0;
                var peVol = PEVolumeQueue.Count > 0 ? PEVolumeQueue.Peek().Volume : 0;
                return (ceVol, peVol, false);
            }
            
            var ceItem = CEVolumeQueue.Peek();
            var peItem = PEVolumeQueue.Peek();
            
            var timeDiff = Math.Abs((ceItem.Timestamp - peItem.Timestamp).TotalMilliseconds);
            
            if (timeDiff <= 200)
            {
                var ceVol = CEVolumeQueue.Dequeue().Volume;
                var peVol = PEVolumeQueue.Dequeue().Volume;
                return (ceVol, peVol, true);
            }
            
            return (ceItem.Volume, peItem.Volume, false);
        }
    }
    
    /// <summary>
    /// Represents a volume item in the queue with timestamp for aging
    /// </summary>
    public class VolumeQueueItem
    {
        public long Volume { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
