﻿using System.Threading.Tasks;

namespace UnitTests.Interfaces
{
    public interface ICSharpBaseInterface
    {
        Task<int> Echo(int x);
    }
}
