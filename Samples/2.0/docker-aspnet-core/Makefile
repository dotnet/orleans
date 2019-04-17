# a Makefile to test this sample on Kubernetes
# you can test it locally (e.g. using Docker for Windows/Mac) or remotely, on your cloud provider of choice
# do not forget to set Azure Storage connection string on Silo/Program.cs and API/Startup.cs

# your Docker registry (we suppose you have logged in and have push access)
REGISTRY ?= docker.io/dgkanatsios
# the context of your local Kubernetes cluster
LOCAL_K8S_CLUSTER=docker-for-desktop
# the contect of your remote Kubernetes cluster
REMOTE_K8S_CLUSTER=aksorleans
# version/tag of the images that will be pushed to Docker Hub
VERSION=0.0.14

API_PROJECT_NAME=orleans-api
SILO_PROJECT_NAME=orleans-silo

# tag of the images that will be pushed for local development
TAG?=$(shell git rev-list HEAD --max-count=1 --abbrev-commit)

buildlocal: 
		docker build -f ./Silo/Dockerfile -t $(SILO_PROJECT_NAME):$(TAG) .
		docker build -f ./API/Dockerfile -t $(API_PROJECT_NAME):$(TAG) .
		docker system prune -f
deploylocal: uselocalcontext
		kubectl config use-context $(LOCAL_K8S_CLUSTER)
		sed -e 's/orleans-silo-image/$(SILO_PROJECT_NAME):$(TAG)/' -e 's/orleans-api-image/$(API_PROJECT_NAME):$(TAG)/' deploy.yaml | kubectl apply -f -
cleandeploylocal: uselocalcontext clean
uselocalcontext:
		kubectl config use-context $(LOCAL_K8S_CLUSTER)

buildremote: 
		docker build -f ./Silo/Dockerfile -t $(REGISTRY)/$(SILO_PROJECT_NAME):$(VERSION) .
		docker build -f ./API/Dockerfile -t $(REGISTRY)/$(API_PROJECT_NAME):$(VERSION) .
		docker system prune -f
pushremote:
		docker push $(REGISTRY)/$(SILO_PROJECT_NAME):$(VERSION)
		docker push $(REGISTRY)/$(API_PROJECT_NAME):$(VERSION)
deployremote: useremotecontext
		sed -e 's,orleans-silo-image,$(REGISTRY)/$(SILO_PROJECT_NAME):$(VERSION),' -e 's,orleans-api-image,$(REGISTRY)/$(API_PROJECT_NAME):$(VERSION),' deploy.yaml | kubectl apply -f -
		# after a few minutes, you can do a `kubectl get service` to get the Public IP endpoint
cleandeployremote: useremotecontext clean
useremotecontext:	
		kubectl config use-context $(REMOTE_K8S_CLUSTER)

clean:
		kubectl delete deployment $(SILO_PROJECT_NAME) $(API_PROJECT_NAME)
		kubectl delete service $(API_PROJECT_NAME)	