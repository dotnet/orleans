# Transaction scope

The scope allows using transaction in a unit of work of a set of code lines, where the ambient transaction can either be joined or suppressed if there exist one. 

The following section illustrates the pattern using scope.

## Create transaction

    await transactionAgent.Transaction(Create, async () =>
    {
        ...
        await grainA.SetPhrase("Hi");
        await grainB.SetNumber(4);
        ...
    });

Scope always create a new transaction (isolated within ambient transaction).

## Create or join transaction

    await transactionAgent.Transaction(CreateOrJoin, async () =>
    {
        ...
        await grainA.SetPhrase("Hi");
        await grainB.SetNumber(4);
        ...
    });

Scope will join ambient transaction, or create a new transaction.

## Join transaction

    await transactionAgent.Transaction(Join, async () =>
    {
        ...
        await grainA.SetPhrase("Hi");
        await grainB.SetNumber(4);
        ...
    });

Scope will join ambient transaction, or fail.

## Suppress transaction

    await transactionAgent.Transaction(Suppress, async () =>
    {
        ...
        await grainA.SetPhrase("Hi");
        await grainB.SetNumber(4);
        ...
    });

Any ambient transaction will not be passed on, nor will transaction be used.

## Supported transaction

    await transactionAgent.Transaction(Supported, async () =>
    {
        ...
        await grainA.SetPhrase("Hi");
        await grainB.SetNumber(4);
        ...
    });

Scope is not transactional but supports transactions. Ambient transaction will be passed on.

## Not allowed

    await transactionAgent.Transaction(NotAllowed, async () =>
    {
        ...
        await grainA.SetPhrase("Hi");
        await grainB.SetNumber(4);
        ...
    });

If scope is created within ambient transaction, it will throw a not supported exception.

# Supported scenarios

The configured scopes can be arbitrarily nested, e.g.,

    await transactionAgent.Transaction(CreateOrJoin, async () =>
    {
        ...
        await grainA.SetPhrase("Hi");
        ...
        await transactionAgent.Transaction(Create, async () =>
        {
            ...
            await grainA.SetPhrase("Hi");
            ...
            await transactionAgent.Transaction(Suppress, async () =>
            {
                ...
                await grainA.DoWork();
                ...
            });
            ...
            await grainB.SetNumber(4);
            ...
        });
        ...
        await grainB.SetNumber(4);
        ...
    });

