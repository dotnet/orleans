---
layout: page
title: Activation Garbage Collection
---
{% include JB/setup %}

The Orleans Team often uses the phrase “Activation Garbage Collection” but that is probably a somewhat misleading term as it might imply it works the same way that .NET garbage collection does, based solely on memory-pressure triggers.

 In the past, we experimented with Activation-GC based on memory usage triggers, but we abandoned that approach due to the unreliable memory usage metrics we could use. 

 We ended up implementing a simple time-based approach to Activation-GC, which seems to be working well during various high traffic stress test runs.

 This approach requires calculating a balance between 

* Request-per-second 
* Memory-per-grain
* Available memory

in order to set the age-out limits.

## How Activation-GC Works
The most common approach is to configure the silo to periodically scan for activations that have not been used at all for some period of time (CollectionAgeLimit). The silo then proactively ages-out those non-active activations by calling the grain’s Deactivate method and remove them from the silo memory. 

**What counts as “being active” for the purpose of grain activation collection**

* receiving a call 
* receiving a reminder

**What does NOT count as “being active” for the purpose of grain activation collection**

* performing a call (to another grain or to an Orleans client)
* timer events 
* arbitrary IO operations or external calls not involving Orleans framework

**Collection Age Limit**

Activations that haven't been used within a period of time are deactivated automatically by the runtime. This period of time is called the collection age limit. 

 The runtime's default collection age limit is 2 hours. 

## Configuration

Unit Suffixes
Any length of time in the configuration XML file may use a suffix that specifies a unit of time:

Suffix  | Unit 
------------- | -------------
none  | millisecond(s)  
ms  | millisecond(s)  
s  | second(s)  
m  | minute(s)  
hr  | hour(s)  



## Specifying the Default (Global) Age Limit

The default collection age limit that applies to all grain types can be customized by adding the OrleansConfiguation/Globals/Application/Defaults/Deactivation element to the OrleansConfiguration.xml file. The following example specifies that all activations that have been idle for 10 minutes or more should be considered eligible for deactivation.

``` xml
<?xml version="1.0" encoding="utf-8"?>
<OrleansConfiguration xmlns="urn:orleans">
  <Globals>
    <Application>
      <Defaults>
        <Deactivation AgeLimit="10m"/>
      </Defaults>
    </Application>
  </Globals>
</OrleansConfiguration>
```

## Specifying per-Type Age Limits

Individual grain types may specify a collection age limit that is independent from the global default, using the OrleansConfiguation/Globals/Application/GrainType/Deactivation element.

 In the following example, activations that have been idle for 10 minutes are eligible for collection, except activations that are instantiations of the MyGrainAssembly.DoNotDeactivateMeOften class, which are not considered collectable unless idle for a full 24 hours:


``` xml
<?xml version="1.0" encoding="utf-8"?>
<OrleansConfiguration xmlns="urn:orleans">
  <Globals>
    <Application>
      <Defaults>
        <Deactivation AgeLimit="10m"/>
      </Defaults>
      <GrainType Type="MyGrainAssembly.DoNotDeactivateMeOften">
        <Deactivation AgeLimit="24hr"/>
      </GrainType>
    </Application>
  </Globals>
</OrleansConfiguration>
```

 Any number of GrainType elements may be specified.

## Configuring Activation Garbage Collection Programmatically

An activation can also configure its own activation GC, by calling the method on the Orleans.Grain base class:

``` csharp
protected void DelayDeactivation(TimeSpan timeSpan)
```

This call will delay deactivation of this activation for at least the specified time duration.

A positive <c>timeSpan</c> value means “prevent GC of this activation for that time span”.
A negative <c>timeSpan</c> value means “unlock, and make this activation available for GC again”.

This call takes priority over any Activation Garbage Collection settings specified in the config.
Please notice that this setting only applies to this particular activation from which it has been called and it does not apply to all possible instances of grains of this type.
