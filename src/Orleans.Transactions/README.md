# Transaction frame

The transaction frame allows a set of code lines to be executed as a unit of work within a transaction.
Any ambient transaction can be either joined or suppressed with the frame, if there exist one.

The frame type is injected with the type `ITransactionFrame` when needed.

The following section illustrates the pattern using frame.

## Create transaction

    await transactionFrame.RunScope(Create, async () =>
    {
        ...
        await grainA.SetPhrase("Hi");
        await grainB.SetNumber(4);
        ...
    });

A new transaction is always created (isolated within ambient transaction).

## Create or join transaction

    await transactionFrame.RunScope(CreateOrJoin, async () =>
    {
        ...
        await grainA.SetPhrase("Hi");
        await grainB.SetNumber(4);
        ...
    });

The transaction will join the ambient transaction, or a new transaction will be created.

## Join transaction

    await transactionFrame.RunScope(Join, async () =>
    {
        ...
        await grainA.SetPhrase("Hi");
        await grainB.SetNumber(4);
        ...
    });

The transaction will join the ambient transaction, or fail.

## Suppress transaction

    await transactionFrame.RunScope(Suppress, async () =>
    {
        ...
        await grainA.SetPhrase("Hi");
        await grainB.SetNumber(4);
        ...
    });

Any ambient transaction will not be passed on, nor will transaction be used.

## Supported transaction

    await transactionFrame.RunScope(Supported, async () =>
    {
        ...
        await grainA.SetPhrase("Hi");
        await grainB.SetNumber(4);
        ...
    });

The frame is not transactional but supports transactions. Ambient transaction will be passed on.

## Not allowed

    await transactionFrame.RunScope(NotAllowed, async () =>
    {
        ...
        await grainA.SetPhrase("Hi");
        await grainB.SetNumber(4);
        ...
    });

A not supported exception will be thrown if the frame is used within any ambient transaction.

# Supported scenarios

The configured transactions can be arbitrarily nested, e.g.,

    await transactionFrame.RunScope(CreateOrJoin, async () =>
    {
        ...
        await grainA.SetPhrase("Hi");
        ...
        await transactionFrame.RunScope(Create, async () =>
        {
            ...
            await grainA.SetPhrase("Hi");
            ...
            await transactionFrame.RunScope(Suppress, async () =>
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

