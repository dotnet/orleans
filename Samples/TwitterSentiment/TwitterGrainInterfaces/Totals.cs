using System;

namespace TwitterGrainInterfaces
{
    /// <summary>
    /// A DTO to return sentiment score for a hashtag 
    /// </summary>
    public class Totals
    {
        public int Positive { get; set; }
        public int Negative { get; set; }
        public int Total { get; set; }
        public string Hashtag { get; set; }
        public DateTime LastUpdated { get; set; }
        public string LastTweet { get; set; }

    }
}
