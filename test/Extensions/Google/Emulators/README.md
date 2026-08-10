# Google Cloud emulators

Firebase does not publish an official Emulator Suite container. Use the official Firebase CLI package instead.

Install Java 21 and Firebase CLI version 15.24.0:

```shell
npm install --global firebase-tools@15.24.0
```

From this directory, start the configured emulators:

```shell
firebase emulators:start --only firestore,pubsub,storage --project orleans-test
```
