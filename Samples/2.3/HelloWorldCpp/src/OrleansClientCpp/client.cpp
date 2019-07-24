#include <iostream>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <string>

#include "coreclrhost.h"

#define ORLEANS_CLIENT_ASSEMBLY "TestClient.dll"

// Define OS-specific items like the CoreCLR library's name and path elements
#if WINDOWS
#include <Windows.h>
#define FS_SEPARATOR "\\"
#define PATH_DELIMITER ";"
#define CORECLR_FILE_NAME "coreclr.dll"
#elif LINUX
#include <dirent.h>
#include <dlfcn.h>
#include <limits.h>
#define FS_SEPARATOR "/"
#define PATH_DELIMITER ":"
#define MAX_PATH PATH_MAX
#if OSX
// For OSX, use Linux defines except that the CoreCLR runtime
// library has a different name
#define CORECLR_FILE_NAME "libcoreclr.dylib"
#else
#define CORECLR_FILE_NAME "libcoreclr.so"
#endif
#endif

// namespace ClientCpp
// {
// Callbacks
typedef void (*oninitialize_callback_ptr)(bool success);
typedef void (*onmessage_callback_ptr)(const char *message);

// OrleansClientWrapper methods
typedef void (*initialize_ptr)(oninitialize_callback_ptr callback);
typedef void (*sayhello_ptr)(onmessage_callback_ptr callback);

void BuildTpaList(const char *directory, const char *extension, std::string &tpaList);

void OnInitialize(bool success);
void OnMessage(const char *message);

sayhello_ptr sayHelloDelegate;

int main(int argc, char *argv[])
{
    // Get the current executable's directory
    // This sample assumes that both CoreCLR and the
    // managed assembly to be loaded are next to this host
    // so we need to get the current path in order to locate those.
    char runtimePath[MAX_PATH];
#if WINDOWS
    GetFullPathNameA(argv[0], MAX_PATH, runtimePath, NULL);
#elif LINUX
    realpath(argv[0], runtimePath);
#endif

    char *last_slash = strrchr(runtimePath, FS_SEPARATOR[0]);
    if (last_slash != NULL)
        *last_slash = 0;

    // Construct the CoreCLR path
    // For this sample, we know CoreCLR's path. For other hosts,
    // it may be necessary to probe for coreclr.dll/libcoreclr.so
    std::string coreClrPath(runtimePath);
    coreClrPath.append(FS_SEPARATOR);
    coreClrPath.append(CORECLR_FILE_NAME);

    // Construct the managed library path
    std::string managedLibraryPath(runtimePath);
    managedLibraryPath.append(FS_SEPARATOR);
    managedLibraryPath.append(ORLEANS_CLIENT_ASSEMBLY);

    //
    // STEP 1: Load CoreCLR (coreclr.dll/libcoreclr.so)
    //
#if WINDOWS
    HMODULE coreClr = LoadLibraryExA(coreClrPath.c_str(), NULL, 0);
#elif LINUX
    void *coreClr = dlopen(coreClrPath.c_str(), RTLD_NOW | RTLD_LOCAL);
#endif
    if (coreClr == NULL)
    {
        printf("ERROR: Failed to load CoreCLR from %s\n", coreClrPath.c_str());
        return -1;
    }
    else
    {
        printf("Loaded CoreCLR from %s\n", coreClrPath.c_str());
    }

    //
    // STEP 2: Get CoreCLR hosting functions
    //
#if WINDOWS
    coreclr_initialize_ptr initializeCoreClr = (coreclr_initialize_ptr)GetProcAddress(coreClr, "coreclr_initialize");
    coreclr_create_delegate_ptr createManagedDelegate = (coreclr_create_delegate_ptr)GetProcAddress(coreClr, "coreclr_create_delegate");
    coreclr_shutdown_ptr shutdownCoreClr = (coreclr_shutdown_ptr)GetProcAddress(coreClr, "coreclr_shutdown");
#elif LINUX
    coreclr_initialize_ptr initializeCoreClr = (coreclr_initialize_ptr)dlsym(coreClr, "coreclr_initialize");
    coreclr_create_delegate_ptr createManagedDelegate = (coreclr_create_delegate_ptr)dlsym(coreClr, "coreclr_create_delegate");
    coreclr_shutdown_ptr shutdownCoreClr = (coreclr_shutdown_ptr)dlsym(coreClr, "coreclr_shutdown");
#endif

    if (initializeCoreClr == NULL)
    {
        printf("coreclr_initialize not found");
        return -1;
    }

    if (createManagedDelegate == NULL)
    {
        printf("coreclr_create_delegate not found");
        return -1;
    }

    if (shutdownCoreClr == NULL)
    {
        printf("coreclr_shutdown not found");
        return -1;
    }

    //
    // STEP 3: Construct properties used when starting the runtime
    //

    // Construct the trusted platform assemblies (TPA) list
    // This is the list of assemblies that .NET Core can load as
    // trusted system assemblies.
    // For this host (as with most), assemblies next to CoreCLR will
    // be included in the TPA list
    std::string tpaList;
    BuildTpaList(runtimePath, ".dll", tpaList);

    // Define CoreCLR properties
    // Other properties related to assembly loading are common here,
    // but for this simple sample, TRUSTED_PLATFORM_ASSEMBLIES is all
    // that is needed. Check hosting documentation for other common properties.
    const char *propertyKeys[] = {
        "TRUSTED_PLATFORM_ASSEMBLIES" // Trusted assemblies
    };

    const char *propertyValues[] = {tpaList.c_str()};

    //
    // STEP 4: Start the CoreCLR runtime
    //
    void *hostHandle;
    unsigned int domainId;

    // This function both starts the .NET Core runtime and creates
    // the default (and only) AppDomain
    int hr = initializeCoreClr(
        runtimePath,                           // App base path
        "OrleansClientCpp",                    // AppDomain friendly name
        sizeof(propertyKeys) / sizeof(char *), // Property count
        propertyKeys,                          // Property names
        propertyValues,                        // Property values
        &hostHandle,                           // Host handle
        &domainId);                            // AppDomain ID

    if (hr >= 0)
    {
        printf("CoreCLR started\n");
    }
    else
    {
        printf("coreclr_initialize failed - status: 0x%08x\n", hr);
        return -1;
    }

    //
    // STEP 5: Create delegates to managed code and invoke it
    //

    initialize_ptr initializeDelegate;

    // The assembly name passed in the third parameter is a managed assembly name
    // as described at https://docs.microsoft.com/dotnet/framework/app-domains/assembly-names
    hr = createManagedDelegate(
        hostHandle,
        domainId,
        "TestClient, Version=1.0.0.0",
        "TestClient.OrleansClientWrapper",
        "Initialize",
        (void **)&initializeDelegate);

    if (hr >= 0)
    {
        printf("Initialize delegate created\n");
    }
    else
    {
        printf("coreclr_create_delegate failed - status: 0x%08x\n", hr);
        return -1;
    }

    sayhello_ptr sayHello;

    // The assembly name passed in the third parameter is a managed assembly name
    // as described at https://docs.microsoft.com/dotnet/framework/app-domains/assembly-names
    hr = createManagedDelegate(
        hostHandle,
        domainId,
        "TestClient, Version=1.0.0.0",
        "TestClient.OrleansClientWrapper",
        "SayHello",
        (void **)&sayHello);

    if (hr >= 0)
    {
        printf("OnMessage delegate created\n");
        sayHelloDelegate = sayHello;
    }
    else
    {
        printf("coreclr_create_delegate failed - status: 0x%08x\n", hr);
        return -1;
    }

    initializeDelegate(OnInitialize);

    std::cout << "Press any key to exit...";
    std::string s;
    getline(std::cin, s);

    //
    // STEP 6: Shutdown CoreCLR
    //

    // <Snippet6>
    hr = shutdownCoreClr(hostHandle, domainId);
    // </Snippet6>

    if (hr >= 0)
    {
        printf("CoreCLR successfully shutdown\n");
    }
    else
    {
        printf("coreclr_shutdown failed - status: 0x%08x\n", hr);
    }

    return 0;
}

void OnInitialize(bool success)
{
    if (!success)
    {
        std::cout << "Failure connecting to Orleans Silo!";
        return;
    }
    sayHelloDelegate(OnMessage);
}

void OnMessage(const char *message)
{
    std::cout << message << " - C++\n";
}

#if WINDOWS
// Win32 directory search for .dll files
// <Snippet7>
void BuildTpaList(const char *directory, const char *extension, std::string &tpaList)
{
    // This will add all files with a .dll extension to the TPA list.
    // This will include unmanaged assemblies (coreclr.dll, for example) that don't
    // belong on the TPA list. In a real host, only managed assemblies that the host
    // expects to load should be included. Having extra unmanaged assemblies doesn't
    // cause anything to fail, though, so this function just enumerates all dll's in
    // order to keep this sample concise.
    std::string searchPath(directory);
    searchPath.append(FS_SEPARATOR);
    searchPath.append("*");
    searchPath.append(extension);

    WIN32_FIND_DATAA findData;
    HANDLE fileHandle = FindFirstFileA(searchPath.c_str(), &findData);

    if (fileHandle != INVALID_HANDLE_VALUE)
    {
        do
        {
            // Append the assembly to the list
            tpaList.append(directory);
            tpaList.append(FS_SEPARATOR);
            tpaList.append(findData.cFileName);
            tpaList.append(PATH_DELIMITER);

            // Note that the CLR does not guarantee which assembly will be loaded if an assembly
            // is in the TPA list multiple times (perhaps from different paths or perhaps with different NI/NI.dll
            // extensions. Therefore, a real host should probably add items to the list in priority order and only
            // add a file if it's not already present on the list.
            //
            // For this simple sample, though, and because we're only loading TPA assemblies from a single path,
            // and have no native images, we can ignore that complication.
        } while (FindNextFileA(fileHandle, &findData));
        FindClose(fileHandle);
    }
}
// </Snippet7>
#elif LINUX
// POSIX directory search for .dll files
void BuildTpaList(const char *directory, const char *extension, std::string &tpaList)
{
    DIR *dir = opendir(directory);
    struct dirent *entry;
    int extLength = strlen(extension);

    while ((entry = readdir(dir)) != NULL)
    {
        // This simple sample doesn't check for symlinks
        std::string filename(entry->d_name);

        // Check if the file has the right extension
        int extPos = filename.length() - extLength;
        if (extPos <= 0 || filename.compare(extPos, extLength, extension) != 0)
        {
            continue;
        }

        // Append the assembly to the list
        tpaList.append(directory);
        tpaList.append(FS_SEPARATOR);
        tpaList.append(filename);
        tpaList.append(PATH_DELIMITER);

        // Note that the CLR does not guarantee which assembly will be loaded if an assembly
        // is in the TPA list multiple times (perhaps from different paths or perhaps with different NI/NI.dll
        // extensions. Therefore, a real host should probably add items to the list in priority order and only
        // add a file if it's not already present on the list.
        //
        // For this simple sample, though, and because we're only loading TPA assemblies from a single path,
        // and have no native images, we can ignore that complication.
    }
}
#endif
//} // namespace ClientCpp