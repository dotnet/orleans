using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Serialization;
using Newtonsoft.Json;

namespace Tester
{
    [TestClass]
    public class InnerTypeSerializationTests
    {
        [TestInitialize]
        public void InitializeForTesting()
        {
            SerializationManager.InitializeForTesting();
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void InnerTypeSerializationTests_DictionaryTest()
        {
            //Client create, serializes and send to the frontend server.
            var payload = new RootType();
            var str = JsonConvert.SerializeObject(payload);

            //The frontend will deserialize the incoming parameter, which will turn all itens on the dictionary into a JObject
            var desPayload = JsonConvert.DeserializeObject<RootType>(str);

            //Grain is called from the frontend server
            var smReturn = SerializationManager.RoundTripSerializationForTesting(desPayload);

            Assert.AreEqual(payload, smReturn);
        }
    }

    public class RootType
    {
        public RootType()
        {
            MyDictionary = new Dictionary<string, object>();
            MyDictionary.Add("obj1", new InnerType());
            MyDictionary.Add("obj2", new InnerType());
            MyDictionary.Add("obj3", new InnerType());
            MyDictionary.Add("obj4", new InnerType());
        }
        public Dictionary<string, object> MyDictionary { get; set; }
    }

    public class InnerType
    {
        public InnerType()
        {
            Id = Guid.NewGuid();
            Something = Id.ToString();
        }
        public Guid Id { get; set; }
        public string Something { get; set; }
    }
}
