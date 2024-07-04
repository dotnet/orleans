using System.Threading.Tasks;

namespace Orleans.Streams
{
    public interface IStreamCheckpointerGrain : IGrainWithStringKey
    {
        /// <summary>
        /// Loads the checkpoint.
        /// </summary>
        /// <returns>The checkpoint.</returns>
        Task<string> Load();

        /// <summary>
        /// Updates the checkpoint.
        /// </summary>
        /// <param name="offset">The offset.</param>
        Task Update(string offset);
    }
}
