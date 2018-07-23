---
documentType: index
title: Microsoft Orleans
tagline: A straightforward approach to building distributed, high-scale applications in .NET
---
<style>
.subtitle {
    font-size:20px;
}
.main_logo {
    width:70%
}
.jumbotron{
    text-align: center;
}
</style>


<div class="jumbotron">
    <div class="container">
      <img src="images/logo.svg" class="main_logo" />
      <h1 class="title"><small class="subtitle">A straightforward approach to building distributed, high-scale applications in .NET</small></h1>
      <div class="options">
        <a class="btn btn-lg btn-primary" href="https://github.com/dotnet/orleans">Go to the Orleans Repo</a> 
      </div>
    </div>
</div>

#Documentation Overview 
NOTE: This is the main landing page that tells readers they have found Orleans documentation.
The information below could probably be removed because it's already in another section.
Also, this page's formatting does not fit into the rest of the documents, style-wise.

<p class="lead">
    Orleans is a framework that provides a straightforward approach to building distributed high-scale computing applications, without the need to learn and apply complex concurrency or other scaling patterns.
    It was created by Microsoft Research and designed for use in the cloud.
</p>

<p class="lead">
    Orleans has been used extensively in Microsoft Azure by several Microsoft product groups, most notably by 343 Industries as a platform for all of Halo 4 and Halo 5 cloud services, as well as by a growing number of other companies.
</p>

---

<div class="row">
    <div class="col-md-4">
        <h3>Scalable by Default</h3>

        Orleans handles the complexity of building distributed systems, enabling your application
        to scale to hundreds of servers.
    </div>
    <div class="col-md-4">
        <h3>Low Latency</h3>

        Orleans allows you to keep the state you need in memory, so your application can rapidly respond
        to incoming requests.
    </div>
    <div class="col-md-4">
        <h3>Simplified Concurrency</h3>

        Orleans allows you to write simple, single threaded C# code, handling concurrency with asynchronous
        message passing between objects (grains).
    </div>
</div>

---

## Where Next?

Discuss your Orleans questions on the [gitter chat room](https://gitter.im/dotnet/orleans).

Fork the code on the [GitHub Respository](https://github.com/dotnet/orleans).
