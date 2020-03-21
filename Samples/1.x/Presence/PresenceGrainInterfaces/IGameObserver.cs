using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Orleans.Samples.Presence.GrainInterfaces
{
    /// <summary>
    /// Observer interface for an external client, such as a console app or a web frontend, to receive updates to the score of a particular game
    /// </summary>
    public interface IGameObserver : IGrainObserver
    {
        void UpdateGameScore(string score);
    }
}
