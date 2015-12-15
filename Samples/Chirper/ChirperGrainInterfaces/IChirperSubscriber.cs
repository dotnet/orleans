using System.Threading.Tasks;

namespace Orleans.Samples.Chirper.GrainInterfaces
{
    /// <summary>
    /// Orleans observer interface IChirperSubscriber
    /// </summary>
    public interface IChirperSubscriber : IGrainWithIntegerKey
    {
        /// <summary>Notification that a new Chirp has been received</summary>
        /// <param name="chirp">Chirp message entry</param>
        Task NewChirp(ChirperMessage chirp);
    }
}
