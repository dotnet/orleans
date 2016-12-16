using Microsoft.Owin;
using Newtonsoft.Json;
using Orleans.Runtime.Scheduler;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace Orleans.Runtime
{

    internal class GrainController
    {
        readonly OrleansTaskScheduler taskScheduler;
        readonly IGrainFactory grainFactory;
        readonly ISchedulingContext schedulingContext;
        readonly ConcurrentDictionary<string, MethodInfo> grainFactoryCache = new ConcurrentDictionary<string, MethodInfo>();
        readonly ConcurrentDictionary<string, MethodInfo> grainMethodCache = new ConcurrentDictionary<string, MethodInfo>();
        readonly ConcurrentDictionary<string, Type> grainTypeCache = new ConcurrentDictionary<string, Type>();

        public GrainController(Router router, OrleansTaskScheduler taskScheduler, IGrainFactory grainFactory, ISchedulingContext schedulingContext)
        {
            this.taskScheduler = taskScheduler;
            this.grainFactory = grainFactory;
            this.schedulingContext = schedulingContext;

            // register routes on the router
            router.Add("/grain/:type/:id/:method", CallGrain);
        }

        async Task CallGrain(IOwinContext context, IDictionary<string, string> parameters)
        {
            var grainTypeName = parameters["type"];
            var grainId = parameters["id"];

            string classPrefix = null;
            if (grainTypeName.Contains(':'))
            {
                // if the grain's type contains an ':' character, then split it 
                // and use the parts as the type and the class prefix

                var grainTypeNameParts = grainTypeName.Split(':');
                grainTypeName = grainTypeNameParts[0];
                classPrefix = grainTypeNameParts[1];
            }

            var grainMethodName = parameters["method"];

            var grainType = GetGrainType(grainTypeName);
            var grainFactory = GetGrainFactory(grainTypeName);
            var grain = GetGrain(grainType, grainFactory, grainId, classPrefix);
            var grainMethod = GetGrainMethod(grainTypeName, grainMethodName, grainType);
            var grainMethodArguments = GrainMethodArguments(grainMethod, context).ToArray();


            Task task = null;
            await this.taskScheduler.QueueAction(async () => 
            {
                task = grainMethod.Invoke(grain, grainMethodArguments) as Task;
                await task;
            }, this.schedulingContext);

            object returnValue = null;
            var resultProperty = task.GetType().GetProperties().FirstOrDefault(x => x.Name == "Result");
            if (null != resultProperty) returnValue = resultProperty.GetValue(task);

            await context.ReturnJson(returnValue);
        }

        MethodInfo GetGrainMethod(string grainTypeName, string grainMethodName, Type grainType)
        {
            var grainMethod = this.grainMethodCache.GetOrAdd($"{grainTypeName}.{grainMethodName}", x => grainType.GetMethod(grainMethodName));
            if (null == grainMethod) throw new MissingMethodException(grainTypeName, grainMethodName);
            return grainMethod;
        }

        IEnumerable<object> GrainMethodArguments(MethodInfo grainMethod, IOwinContext context)
        {
            foreach (var param in grainMethod.GetParameters())
            {
                var value = context.Request.Query[param.Name];
                if (null == value)
                {
                    yield return null;
                    continue;
                }
                yield return JsonConvert.DeserializeObject(value, param.ParameterType);
            }
        }

        async Task<object> Dispatch(Func<Task<object>> func)
        {
            object returnValue = null;
            await this.taskScheduler.QueueAction(async () => returnValue = await func(), this.schedulingContext);
            return returnValue;
            //return await Task.Factory.StartNew(func, CancellationToken.None, TaskCreationOptions.None, scheduler: this.taskScheduler);
        }

        // horrible way of getting the correct method to get a grain reference
        // this could be optimised further by returning this as a closure when getting the factory methodinfo
        object GetGrain(Type grainType, MethodInfo grainFactoryMethod, string id, string classPrefix)
        {
            if (typeof(IGrainWithGuidKey).IsAssignableFrom(grainType))
            {
                return grainFactoryMethod.Invoke(this.grainFactory, new object[] { Guid.Parse(id), classPrefix });
            }
            if (typeof(IGrainWithIntegerKey).IsAssignableFrom(grainType))
            {
                return grainFactoryMethod.Invoke(this.grainFactory, new object[] { long.Parse(id), classPrefix });
            }

            if (typeof(IGrainWithStringKey).IsAssignableFrom(grainType))
            {
                return grainFactoryMethod.Invoke(this.grainFactory, new object[] { id, classPrefix });
            }

            if (typeof(IGrainWithGuidCompoundKey).IsAssignableFrom(grainType))
            {
                var parts = id.Split(',');
                return grainFactoryMethod.Invoke(this.grainFactory, new object[] { Guid.Parse(parts[0]), parts[1], classPrefix });
            }
            if (typeof(IGrainWithIntegerCompoundKey).IsAssignableFrom(grainType))
            {
                var parts = id.Split(',');
                return grainFactoryMethod.Invoke(this.grainFactory, new object[] { long.Parse(parts[0]), parts[1], classPrefix });
            }

            throw new NotSupportedException($"cannot construct grain {grainType.Name}");
        }


        Type GetGrainType(string grainTypeName)
        {
            return grainTypeCache.GetOrAdd(grainTypeName, GetGrainTypeViaReflection);
        }

        Type GetGrainTypeViaReflection(string grainTypeName)
        {
            var grainType = AppDomain.CurrentDomain.GetAssemblies().SelectMany(x => x.GetTypes()).Where(x => x.FullName == grainTypeName).FirstOrDefault();
            if (null == grainType) throw new ArgumentException($"Grain type not found '{grainTypeName}'");
            return grainType;
        }


        MethodInfo GetGrainFactory(string grainTypeName)
        {
            return this.grainFactoryCache.GetOrAdd(grainTypeName, GetGrainFactoryViaReflection);
        }

        MethodInfo GetGrainFactoryViaReflection(string grainTypeName)
        {
            var grainType = GetGrainType(grainTypeName);
            var methods = this.grainFactory.GetType().GetMethods().Where(x => x.Name == "GetGrain");

            if (typeof(IGrainWithGuidKey).IsAssignableFrom(grainType))
            {
                var method = methods.First(x => x.GetParameters().Length == 2 && x.GetParameters().First().ParameterType.Name == "System.Guid");
                return method.MakeGenericMethod(grainType);
            }
            if (typeof(IGrainWithIntegerKey).IsAssignableFrom(grainType))
            {
                var method = methods.First(x => x.GetParameters().Length == 2 && x.GetParameters().First().ParameterType.Name == "Int64");
                return method.MakeGenericMethod(grainType);
            }

            if (typeof(IGrainWithStringKey).IsAssignableFrom(grainType))
            {
                var method = methods.First(x => x.GetParameters().Length == 2 && x.GetParameters().First().ParameterType.Name == "String");
                return method.MakeGenericMethod(grainType);
            }

            if (typeof(IGrainWithGuidCompoundKey).IsAssignableFrom(grainType))
            {
                var method = methods.First(x => x.GetParameters().Length == 3 && x.GetParameters().First().ParameterType.Name == "System.Guid");
                return method.MakeGenericMethod(grainType);
            }
            if (typeof(IGrainWithIntegerCompoundKey).IsAssignableFrom(grainType))
            {
                var method = methods.First(x => x.GetParameters().Length == 3 && x.GetParameters().First().ParameterType.Name == "Int64");
                return method.MakeGenericMethod(grainType);
            }

            throw new NotSupportedException($"cannot construct grain {grainType.Name}");
        }

    }
}
