using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;

namespace UnitTestGrainInterfaces
{
    [Serializable]
    public class ComplicatedTestType<T>
    {
        public T Basic { get; set; }
        public T[] BasicArray { get; set; }
        public T[][] BasicMultiArray { get; set; }
        public List<T> BasicList { get; set; }
        public List<List<T>> BasicListOfList { get; set; }
        public List<T[]> BasicListOfArray { get; set; }
        public List<T>[] BasicArrayOfList { get; set; }
        public void InitWithSeed(T seed)
        {
            Basic = seed;
            
            BasicArray = new T[3];
            for (int i = 0; i < 3; i++)
            {
                BasicArray[i] = seed ;
            }

            BasicMultiArray = new T[2][];
            for (int i = 0; i < 2; i++)
            {
                BasicMultiArray[i] = new T[2];
                for (int j = 0; j < 2; j++)
                {
                    BasicMultiArray[i][j] = seed;
                }
            }
            
            BasicList = new List<T>();
            for (int i = 0; i < 5; i++)
            {
                BasicList.Add(seed);
            }

            BasicListOfList = new List<List<T>>();
            for (int i = 0; i < 2; i++)
            {
                List<T> lst = new List<T>();
                for (int j = 0; j < 2; j++)
                {
                    lst.Add(seed);
                }
                BasicListOfList.Add(lst);
            }

            BasicArrayOfList = new List<T>[2];
            for (int i = 0; i < 2; i++)
            {
                List<T> lst = new List<T>();
                for (int j = 0; j < 2; j++)
                {
                    lst.Add(seed);
                }
                BasicArrayOfList[i] =lst;
            }

            BasicListOfArray = new List<T[]>();
            for (int i = 0; i < 2; i++)
            {
                T[] arr = new T[2];
                for (int j = 0; j < 2; j++)
                {
                    arr[j] = seed;
                }
                BasicListOfArray.Add(arr);
            }
        }
    }
    public interface IComplexGrain : IGrain
    {
        Task<ComplicatedTestType<int>> GetFldInt();
        Task SeedFldInt(int i);

        Task<ComplicatedTestType<string>> GetFldStr();
        Task SeedFldStr(string s);
    }
    public interface ILinkedListGrain : IGrain
    {
        Task<ILinkedListGrain> GetNext();
        Task<int> GetValue();
        Task SetValue(int v);
        Task SetNext(ILinkedListGrain next);
    }

}
