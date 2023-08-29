# Google Cloud Emulators

## To run google's emulators either run the following Docker Containers or install the Google Cloud Firebase tools

### Docker Containers

```bash
docker run --name gcp-firestore-emulator -d -p 9595:9595 gcr.io/google.com/cloudsdktool/google-cloud-cli:emulators gcloud emulators firestore start --host-port=0.0.0.0:9595 --project=orleans-test
docker run --name gcp-pubsub-emulator -d -p 9596:9596 gcr.io/google.com/cloudsdktool/google-cloud-cli:emulators gcloud beta emulators pubsub start --host-port=0.0.0.0:9596 --project=orleans-test
```

> Note: When using Docker, the Google Cloud Storage emulator is not available.

### Google Cloud Firebase Tools

From the `test/Extensions/Google.Tests/Emulators` directory run the following commands:
```bash
curl -sL firebase.tools | bash # Install the firebase tools (if not already installed)
firebase setup:emulators:storage # Install the storage emulator (if not already installed)
firebase setup:emulators:firestore # Install the firestore emulator (if not already installed)
firebase setup:emulators:pubsub # Install the pubsub emulator (if not already installed)
firebase emulators:start --only firestore,pubsub,storage --project orleans-test # Start the emulators
``` 
