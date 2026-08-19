# OrleansWebApp

This ASP.NET Core application co-hosts an Orleans silo. The HTTP endpoint resolves `IGrainFactory` from dependency injection and calls a grain in the same cluster.

Run the application:

```dotnetcli
dotnet run
```

Call the generated endpoint:

```console
curl http://localhost:5000/hello/Ada
```

The endpoint returns `Hello, Ada!`.

The generated app uses localhost clustering for local development. Configure a shared clustering provider, durable storage, and production endpoints before deploying multiple instances.
