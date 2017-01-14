using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.EventSourcing.Common
{
    public static class StringEncodedWriteVector
    {

        // BitVector of replicas is implemented as a set of replica strings encoded within a string
        // The bitvector is represented as the set of replica ids whose bit is 1
        // This set is written as a string that contains the replica ids preceded by a comma each
        //
        // Assuming our replicas are named A, B, and BB, then
        // ""     represents    {}        represents 000 
        // ",A"   represents    {A}       represents 100 
        // ",A,B" represents    {A,B}     represents 110 
        // ",BB,A,B" represents {A,B,BB}  represents 111 

        /// <summary>
        /// Gets one of the bits in writeVector
        /// </summary>
        /// <param name="Replica">The replica for which we want to look up the bit</param>
        /// <returns></returns>
        public static bool GetBit(string writeVector, string Replica)
        {
            var pos = writeVector.IndexOf(Replica);
            return pos != -1 && writeVector[pos - 1] == ',';
        }

        /// <summary>
        /// toggle one of the bits in writeVector and return the new value.
        /// </summary>
        /// <param name="writeVector">The write vector in which we want to flip the bit</param>
        /// <param name="Replica">The replica for which we want to flip the bit</param>
        /// <returns>the state of the bit after flipping it</returns>
        public static bool FlipBit(ref string writeVector, string Replica)
        {
            var pos = writeVector.IndexOf(Replica);
            if (pos != -1 && writeVector[pos - 1] == ',')
            {
                var pos2 = writeVector.IndexOf(',', pos + 1);
                if (pos2 == -1)
                    pos2 = writeVector.Length;
                writeVector = writeVector.Remove(pos - 1, pos2 - pos + 1);
                return false;
            }
            else
            {
                writeVector = string.Format(",{0}{1}", Replica, writeVector);
                return true;
            }
        }
    }
}
