---
layout: page
title: Scheduler
---

# Scheduler

Orleans Scheduler is a component within the Orleans runtime responsible for executing application code and parts of the runtime code to ensure the **single threaded execution semantics**. It implements a custom TPL Task scheduler.

Orleans Task scheduler is a hierarchical 2 level scheduler. 
At the first level there is the global **OrleansTaskScheduler** that is responsible for execution of system activities. 
At the second level every grain activation has its own **ActivationTaskScheduler**, which provides the single threaded execution semantics.

### At a high level, the execution path is the following:
1.	A request arrives to the correct silo and the destination activation is found.
2.	A request is translated into a Task that is queued for execution by that activation, on its ActivationTaskScheduler.
3.	Any subsequent Task created as part of the grain method execution is natively enqueued to the same ActivationTaskScheduler, via the standard TaskScheduler mechanism.
4.	Every ActivationTaskScheduler has a queue of tasks queued for execution.
5.	Orleans Scheduler has a set of worker threads that are collectively used by all the activation schedulers. Those threads periodically scan all the scheduler queues for work to execute. 
6.	A thread takes a queue (each queue is taken by one thread at a time) and starts executing Tasks in that queue in FIFO order.
7. The combination of one thread at a time taking a queue and the thread executing Tasks sequentially is what provides the single threaded execution semantics.

### Work Items:
Orleans uses a notion of Work Items to designate the entry point into the scheduler. Every new request is enqueued initially as a work item which simply wraps the execution of the first Task for that request. Work items simply provide more contextual information about the scheduling activity (the caller, the name of the activity, logging) and sometimes some extra work that has to be done on behalf of that scheduling activity (post invocation activity in Invoke work item).
There are currently the following work item types:
1.	Invoke work item – this is the mostly frequently used work item type. It represents execution of an application request. 
2.	Request/Response work items – executes a system request (request to a SystemTarget) 
3.	TaskWorkItem – represent a Task queued to the top level OrleansTaskScheduler. Used instead of a direct Task just for convenience of data structures (more details below).
4.	WorkItemGroup – group of work items that share the same scheduler. Used to wrap a queue of Tasks for each ActivationTaskScheduler.
5.	ClosureWorkItem – a wrapper around a closure (arbitrary lambda) that is queued to the system context.

### Scheduling Context:
Scheduling Context is a tag, just an opaque object that represents scheduling target – activation data, system target or system null context.


### High level Principles:
1.	Tasks are always queued to the correct scheduler

    1.1	Tasks are never moved around from one scheduler to another. 
    
    1.2	We never create tasks on behalf of other tasks to execute them.
    
    1.3	WorkItems are wrapped within Task (that is, in order to execute a work item, we create a Task whose lambda function will just run the work item lambda). By always going via tasks we ensure that any activity is executed via an appropriate Task scheduler.
    
2.	Tasks are executed on the scheduler where they were queued by using base.TryExecute (and not by RunSynchronously)
3.	There is a one to one mapping between ATS, WorkItem Group and Scheduling Context:

    3.1	Activation Task Scheduler (ATS) is a custom TPL scheduler. We keep ATS thin and store all the data in WorkItemGroup. ATS points to its WorkItemGroup.
    
    3.2	WorkItem Group is the actual holder (data object) of the activation Tasks. The Tasks are stored in a List<Task> - the queue of all tasks for its ATS. WorkItemGroup points back to its ATS.


### Data Flow and Execution of Tasks and Work items:
1.	The entry point is always a work item enqueued into OrleansTaskScheduler. It can be one of the Invoke/Request/Response/Closure WorkItem.
2.	Wrapped into a Task and enqueued into the correct ActivationTaskScheduler based on the context via Task.Start.
3.	A Task that is queued to its ActivationTaskScheduler is put into the WorkItemGroup queue.
4.	When a Task is put into a WorkItemGroup queue, WorkItemGroup makes sure it appears in OrleansTaskScheduler global RunQueue. RunQueue is the global queue of runnable WorkItemGroups, those that have at least one Task queued, and thus ready to be executed. 
5.	Worker threads scan the RunQueue of OrleansTaskScheduler which hold WorkItemGroups and call WorkItemGroups.Execute 
6.	WorkItemGroups.Execute scans the queue of its tasks and executes them via ActivationTaskScheduler.RunTask(Task)
    6.1	ActivationTaskScheduler.RunTask(Task) calls base.TryExecute.
    6.2	Task that were enqueued directly to the scheduler via TPL will just execute
    6.3	Tasks that wrap work items will call workItem.Execute which will execute the Closure work item delegate.



### Low level design – Work Items:
1.	Queueing work items to OrleansTaskScheduler is how the whole chain of execution for every request starts in the Orleans runtime. This is our entry point into the Scheduler.
2.	Work items are first submitted to OrleansTaskScheduler (since this is the interface presented to the rest of the system).
    2.1	Only closure/invoke/resume work items can be submitted this way. 
    2.2	TaskWorkItem cannot be submitted to OrleansTaskScheduler directly (read more below on handling of TaskWorkItem).
3.	Every work item must be wrapped into Task and enqueued to the right scheduler via Task.Start.
    3.1	This will make sure the TaskScheduler.Current is set correctly on any Task that is created implicitly during execution of this workItem.
    3.2	Wrapping is done by creating a Task via WrapWorkItemAsTask that will execute the work item and enqueuing it to the right scheduler via Task.Start(scheduler).
    3.3	Work items for the null context are queued to OrleansTaskScheduler.
    3.4	Work items for non-null contexts are queued to ActivationTaskScheduler 
 
### Low level design – Queueing Tasks:
1.	Tasks are queued directly to the right scheduler
    1.1	Tasks are queued implicitly by TPL via protected override void QueueTask(Task task)
    1.2	A Task that has a non-null context is always enqueued to ActivationTaskScheduler 
    1.3	A Task that has the null context is always enqueued to OrleansTaskScheduler
2.	Queueing Tasks to ActivationTaskScheduler:
    2.1	We never wrap a Task in another Task. A Task gets added directly to the WorkItem Group queue
3. Queueing Tasks to OrleansTaskScheduler:
    3.1	When a Task is enqueued to the OrleansTaskScheduler, we wrap it into a TaskWorkItem and put it into this scheduler’s queue of work items. 
    3.2	This is just a matter of data structures, nothing inherent about it:
    3.3	OrleansTaskScheduler usually holds work item groups to schedule them, so its RunQueue has a BlockingCollection<IWorkItem>.
    3.4	Since tasks to the null context are also queued to OrleansTaskScheduler, we reuse the same data structure, thus we have to wrap each Task in a TaskWorkItem.
    3.5	We should be able to get rid of this wrapping completely by adjusting the RunQueue data structure. This may simplify the code a bit, but in general should not matter. Also, in the future we should move away from the null context anyway, so this issue will be gone anyway
 

### Inlining tasks:
Since Tasks are always queued to the right scheduler, in theory it should always be safe to inline any Task. 

