# Backward compatibility guidelines

Writing backward compatible code can be hard and difficult to test.

## Never change the signature of existing methods

Because of the way on how Orleans serializer work, you should never change the signature
of existing methods.

The following example is correct:

``` cs
[Version(1)]
public interface IMyGrain : IGrainWithIntegerKey
{
  // First method
  Task MyMethod(int arg);
}
```
``` cs
[Version(2)]
public interface IMyGrain : IGrainWithIntegerKey
{
  // Method inherited from V1
  Task MyMethod(int arg);

  // New method added in V2
  Task MyNewMethod(int arg, obj o);
}
```

This is not correct:
``` cs
[Version(1)]
public interface IMyGrain : IGrainWithIntegerKey
{
  // First method
  Task MyMethod(int arg);
}
```
``` cs
[Version(2)]
public interface IMyGrain : IGrainWithIntegerKey
{
  // Method inherited from V1
  Task MyMethod(int arg, obj o);
}
```

**NOTE**: you should not do this change in your code, as it's an example of a bad practice that leads to very bad side-effects.
This is an example of what can happen if you just rename the parameter names: let's say
that we have the two following interface version deployed in the cluster:
``` cs
[Version(1)]
public interface IMyGrain : IGrainWithIntegerKey
{
  // return a - b
  Task<int> Substract(int a, int b);
}
```
``` cs
[Version(2)]
public interface IMyGrain : IGrainWithIntegerKey
{
  // return y - x
  Task<int> Substract(int y, int x);
}
```

This methods seems identical. But if the client was called with V1, and the request is
handled by a V2 activation:
``` cs
var grain = client.GetGrain<IMyGrain>(0);
var result = await grain.Substract(5, 4); // Will return "-1" instead of expected "1"
```

This is due to how the internal Orleans serializer works.

## Avoid changing existing method logic

It can seems obvious, but you should be very careful when changing the body of an existing method.
Unless you are fixing a bug, it is better to just add a new method if you need to modify the code.
(see [compatible grains](Compatible-grains.md))

Example:
``` cs
// V1
public interface MyGrain : IMyGrain
{
  // First method
  Task MyMethod(int arg)
  {
    SomeSubRoutine(arg);
  }
}
```
``` cs
// V2
public interface MyGrain : IMyGrain
{
  // Method inherited from V1
  // Do not change the body
  Task MyMethod(int arg)
  {
    SomeSubRoutine(arg);
  }

  // New method added in V2
  Task MyNewMethod(int arg)
  {
    SomeSubRoutine(arg);
    NewRoutineAdded(arg);
  }
}
```

## Do not remove methods from grain interfaces

Unless you are sure that they are no longer used, you should not remove methods from the grain interface.
If you want to remove methods, this should be done in 2 steps:
1. Deploy V2 grains, with V1 method marked as `Obsolete`

  ``` cs
  [Version(1)]
  public interface IMyGrain : IGrainWithIntegerKey
  {
    // First method
    Task MyMethod(int arg);
  }
  ```
  ``` cs
  [Version(2)]
  public interface IMyGrain : IGrainWithIntegerKey
  {
    // Method inherited from V1
    [Obsolete]
    Task MyMethod(int arg);

    // New method added in V2
    Task MyNewMethod(int arg, obj o);
  }
  ```

2. When you are sure that no V1 calls are made (effectively V1 is no longer deployed in the running cluster), deploy V3 with V1 method removed
  ``` cs
  [Version(3)]
  public interface IMyGrain : IGrainWithIntegerKey
  {
    // New method added in V2
    Task MyNewMethod(int arg, obj o);
  }
  ```
