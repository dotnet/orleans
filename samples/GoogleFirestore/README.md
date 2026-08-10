# Google Cloud Firestore providers

This sample runs an Orleans silo and client in one process and configures Google Cloud Firestore for:

- Cluster membership and gateway discovery.
- The default grain directory.
- Persistent grain state.
- Durable reminders.

Each run increments a persisted counter and creates or updates a reminder.

## Run with the Firestore emulator

Install Java 21 and the Firebase CLI, then start Firestore from the repository root:

```powershell
npx firebase-tools@15.24.0 emulators:start --only firestore --project orleans-sample --config test/Extensions/Google/Emulators/firebase.json
```

In another terminal:

```powershell
$env:GOOGLE_CLOUD_PROJECT = "orleans-sample"
$env:FIRESTORE_EMULATOR_HOST = "127.0.0.1:8080"
dotnet run --project samples/GoogleFirestore
```

Run the app again to observe the persisted counter increment.

## Run with Google Cloud

Create a Firestore database in Native mode, authenticate using Application Default Credentials, and set the project ID:

```powershell
gcloud auth application-default login
$env:GOOGLE_CLOUD_PROJECT = "your-project-id"
Remove-Item Env:FIRESTORE_EMULATOR_HOST -ErrorAction Ignore
dotnet run --project samples/GoogleFirestore
```

The authenticated identity needs Firestore permissions to create, query, update, and delete documents. The sample writes beneath the `OrleansSample` root collection.
