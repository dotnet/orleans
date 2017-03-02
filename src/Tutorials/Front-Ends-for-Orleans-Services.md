---
layout: page
title: Front Ends for Orleans Services
---

# Front Ends for Orleans Services

Exposing silo gateway ports as public endpoints of an Orleans cluster is not recommended.
Instead, Orleans is intended to be fronted by your own API.

Creating an HTTP API, or web application is a common scenario.
Let's extend the Employee/Manager scenario from the  [Declarative-Persistence](Declarative-Persistence.md) walk-through to see what steps are required to publish grain data over HTTP.

## Creating the ASP.NET application
First, you should add a new ASP.NET Web Application to your solution. Then, select the Web API template, although you could use MVC or Web Forms.


## Initializing Orleans

Next, add a reference to the _Orleans.dll_ file in the project references.

As with the Orleans host we created earlier, we need to initialize Orleans.
This is best done in the _Global.asax.cs_ file like this:

``` csharp
namespace WebApplication1
{
    public class WebApiApplication : System.Web.HttpApplication
    {
        protected void Application_Start()
        {
            ...
            var config = ClientConfiguration.LocalhostSilo();

           // Attempt to connect a few times to overcome transient failures and to give the silo enough 
            // time to start up when starting at the same time as the client (useful when deploying or during development).
            const int initializeAttemptsBeforeFailing = 5;

            int attempt = 0;
            while (true)
            {
                try
                {
                    GrainClient.Initialize(config);
                    break;
                }
                catch (SiloUnavailableException e)
                {
                    attempt++;
                    if (attempt >= initializeAttemptsBeforeFailing)
                    {
                         throw;
                    }
                    Thread.Sleep(TimeSpan.FromSeconds(2));
                }
            }

       	   ...
```


Now when the ASP.NET application starts, it will initialize the Orleans Client.

## Creating the Controller

Now lets add a controller to the project, to receive HTTP requests, and call the grain code.

Right click on the "Controllers" folder, and add a new "Web API 2 Controller - Empty".

Next, call the controller `EmployeeController`.

This will create a new empty controller called `EmployeeController`.

We can add a `Get` method to the controller, which we'll use to return the level of an Employee.

``` csharp
public class EmployeeController : ApiController
{
    public Task<int> Get(Guid id)
    {
        var employee = GrainClient.GrainFactory.GetGrain<IEmployee>(id);
        return employee.GetLevel();
    }
}
```

Note that the controller is asynchronous, and we can just pass back the `Task` which the grain returns.

## Running the Application

Now let's test the application.

Build the project. Set the ASP.NET application and the silo host project as the startup projects, and run them.

If you navigate to the API URL (the number may be different on your project)...

    http://localhost:6858/api/employee/42783519-d64e-44c9-9c29-399e3afaa625


 ...you should see the result returned from the grain:

```xml
    42
```

That's the basics in place, the rest of the API can be completed by adding the rest of the HTTP verbs.

## Next

We'll look at how you can deploy Orleans in the Azure Cloud

[Cloud Deployment](Cloud-Deployment.md)
