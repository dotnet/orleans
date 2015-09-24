Instructions for running OrleansManager:

1)	Edit the ClientConfiguration.xml to point to the correct Azure deployment and data connection string.
2)	Edit the runOrleansManager.cmd:
	a.	The first argument is the command to execute: lookup or delete. Make sure to execute lookup!!
	b.	The second argument is the grain type code. You can find it in the generated code. 	
		For example, for SimplePersistentGrain find SimplePersistentGrainReference.InterfaceId:
		protected override int InterfaceId
            	{
                	get
                	{
                    	return 1227585576;
                	}
            	}
		1227585576 is the type code for this grain.  
	c. The third argument is the primary key (long, for example -43234556417698415) or guid (for example 0dcb580b-d734-49d0-a4c8-3af0660ad66e) that you pass to GetGrain().  
4)	RDP into any dispatcher or silo machine in your Azure deployment and copy the OrleansManager whole folder 
5)	Execute runOrleansManager.cmd from within OrleansManager folder.
6)	Collect the output of the tool to send to me.


