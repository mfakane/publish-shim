#include <windows.h>
#include <stdint.h>
#include <string.h>
#include <wchar.h>

#define PUBLISH_SHIM_MAGIC 0x4D485350u
#define PUBLISH_SHIM_VERSION 1u

#pragma pack(push, 1)
typedef struct PublishShimTrailer
{
	uint32_t Magic;
	uint16_t Version;
	uint16_t Flags;
	uint32_t ConfigLength;
} PublishShimTrailer;
#pragma pack(pop)

static void ShowErrorMessage(const wchar_t* title, const wchar_t* detail)
{
	MessageBoxW(NULL, detail, title, MB_OK | MB_ICONERROR | MB_SETFOREGROUND);
}

static BOOL IsDriveLetter(wchar_t ch)
{
	return (ch >= L'A' && ch <= L'Z') || (ch >= L'a' && ch <= L'z');
}

static BOOL IsAbsolutePath(const wchar_t* path)
{
	if (path == NULL || path[0] == L'\0')
	{
		return FALSE;
	}

	if (path[0] == L'\\' || path[0] == L'/')
	{
		return TRUE;
	}

	return IsDriveLetter(path[0]) && path[1] == L':';
}

static BOOL ReadFileExact(HANDLE handle, void* buffer, DWORD size)
{
	BYTE* current = (BYTE*)buffer;

	while (size > 0)
	{
		DWORD chunkRead = 0;
		if (!ReadFile(handle, current, size, &chunkRead, NULL))
		{
			return FALSE;
		}

		if (chunkRead == 0)
		{
			return FALSE;
		}

		current += chunkRead;
		size -= chunkRead;
	}

	return TRUE;
}

static BOOL LoadTargetRelativePath(const wchar_t* selfPath, wchar_t** targetRelativePath)
{
	HANDLE handle = INVALID_HANDLE_VALUE;
	LARGE_INTEGER fileSize;
	LARGE_INTEGER trailerOffset;
	LARGE_INTEGER configOffset;
	PublishShimTrailer trailer;
	DWORD pathCharCount = 0;
	wchar_t* pathBuffer = NULL;
	BOOL success = FALSE;

	*targetRelativePath = NULL;

	handle = CreateFileW(selfPath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
	if (handle == INVALID_HANDLE_VALUE)
	{
		ShowErrorMessage(L"PublishShim", L"Failed to open the shim executable.");
		return FALSE;
	}

	if (!GetFileSizeEx(handle, &fileSize))
	{
		ShowErrorMessage(L"PublishShim", L"Failed to read the shim executable size.");
		goto Cleanup;
	}

	if (fileSize.QuadPart < (LONGLONG)sizeof(PublishShimTrailer))
	{
		ShowErrorMessage(L"PublishShim", L"The shim configuration trailer is missing.");
		goto Cleanup;
	}

	trailerOffset.QuadPart = fileSize.QuadPart - (LONGLONG)sizeof(PublishShimTrailer);
	if (!SetFilePointerEx(handle, trailerOffset, NULL, FILE_BEGIN))
	{
		ShowErrorMessage(L"PublishShim", L"Failed to seek to the shim trailer.");
		goto Cleanup;
	}

	if (!ReadFileExact(handle, &trailer, (DWORD)sizeof(trailer)))
	{
		ShowErrorMessage(L"PublishShim", L"Failed to read the shim trailer.");
		goto Cleanup;
	}

	if (trailer.Magic != PUBLISH_SHIM_MAGIC)
	{
		ShowErrorMessage(L"PublishShim", L"The shim trailer magic value is invalid.");
		goto Cleanup;
	}

	if (trailer.Version != PUBLISH_SHIM_VERSION)
	{
		ShowErrorMessage(L"PublishShim", L"The shim trailer version is not supported.");
		goto Cleanup;
	}

	if (trailer.ConfigLength < sizeof(DWORD) ||
		(ULONGLONG)trailer.ConfigLength > (ULONGLONG)(fileSize.QuadPart - (LONGLONG)sizeof(PublishShimTrailer)))
	{
		ShowErrorMessage(L"PublishShim", L"The shim configuration size is invalid.");
		goto Cleanup;
	}

	configOffset.QuadPart = trailerOffset.QuadPart - trailer.ConfigLength;
	if (!SetFilePointerEx(handle, configOffset, NULL, FILE_BEGIN))
	{
		ShowErrorMessage(L"PublishShim", L"Failed to seek to the shim configuration.");
		goto Cleanup;
	}

	if (!ReadFileExact(handle, &pathCharCount, sizeof(pathCharCount)))
	{
		ShowErrorMessage(L"PublishShim", L"Failed to read the target path length.");
		goto Cleanup;
	}

	if (pathCharCount == 0 ||
		pathCharCount > (trailer.ConfigLength - sizeof(DWORD)) / sizeof(wchar_t) ||
		trailer.ConfigLength != sizeof(DWORD) + pathCharCount * sizeof(wchar_t))
	{
		ShowErrorMessage(L"PublishShim", L"The target path payload is invalid.");
		goto Cleanup;
	}

	pathBuffer = (wchar_t*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, ((SIZE_T)pathCharCount + 1) * sizeof(wchar_t));
	if (pathBuffer == NULL)
	{
		ShowErrorMessage(L"PublishShim", L"Failed to allocate memory for the target path.");
		goto Cleanup;
	}

	if (!ReadFileExact(handle, pathBuffer, pathCharCount * sizeof(wchar_t)))
	{
		ShowErrorMessage(L"PublishShim", L"Failed to read the target path.");
		goto Cleanup;
	}

	if (IsAbsolutePath(pathBuffer))
	{
		ShowErrorMessage(L"PublishShim", L"The configured target path must be relative.");
		goto Cleanup;
	}

	*targetRelativePath = pathBuffer;
	pathBuffer = NULL;
	success = TRUE;

Cleanup:
	if (pathBuffer != NULL)
	{
		HeapFree(GetProcessHeap(), 0, pathBuffer);
	}

	if (handle != INVALID_HANDLE_VALUE)
	{
		CloseHandle(handle);
	}

	return success;
}

static BOOL GetExecutableDirectory(const wchar_t* selfPath, wchar_t** directory)
{
	SIZE_T length = wcslen(selfPath);
	wchar_t* buffer = (wchar_t*)HeapAlloc(GetProcessHeap(), 0, (length + 1) * sizeof(wchar_t));
	SIZE_T index;

	if (buffer == NULL)
	{
		return FALSE;
	}

	memcpy(buffer, selfPath, (length + 1) * sizeof(wchar_t));

	for (index = length; index > 0; --index)
	{
		if (buffer[index - 1] == L'\\' || buffer[index - 1] == L'/')
		{
			buffer[index - 1] = L'\0';
			*directory = buffer;
			return TRUE;
		}
	}

	HeapFree(GetProcessHeap(), 0, buffer);
	return FALSE;
}

static BOOL CombinePath(const wchar_t* left, const wchar_t* right, wchar_t** combined)
{
	SIZE_T leftLength = wcslen(left);
	SIZE_T rightLength = wcslen(right);
	SIZE_T totalLength = leftLength + 1 + rightLength;
	wchar_t* buffer = (wchar_t*)HeapAlloc(GetProcessHeap(), 0, (totalLength + 1) * sizeof(wchar_t));

	if (buffer == NULL)
	{
		return FALSE;
	}

	memcpy(buffer, left, leftLength * sizeof(wchar_t));
	buffer[leftLength] = L'\\';
	memcpy(buffer + leftLength + 1, right, rightLength * sizeof(wchar_t));
	buffer[totalLength] = L'\0';

	*combined = buffer;
	return TRUE;
}

static BOOL GetModulePath(wchar_t** selfPath)
{
	DWORD capacity = MAX_PATH;
	wchar_t* buffer = NULL;

	for (;;)
	{
		DWORD result = 0;
		wchar_t* nextBuffer = (wchar_t*)HeapAlloc(GetProcessHeap(), 0, capacity * sizeof(wchar_t));
		if (nextBuffer == NULL)
		{
			HeapFree(GetProcessHeap(), 0, buffer);
			return FALSE;
		}

		HeapFree(GetProcessHeap(), 0, buffer);
		buffer = nextBuffer;
		result = GetModuleFileNameW(NULL, buffer, capacity);
		if (result == 0)
		{
			HeapFree(GetProcessHeap(), 0, buffer);
			return FALSE;
		}

		if (result < capacity)
		{
			*selfPath = buffer;
			return TRUE;
		}

		if (capacity > (DWORD)(0x7fffffff / 2))
		{
			HeapFree(GetProcessHeap(), 0, buffer);
			return FALSE;
		}

		capacity *= 2;
	}
}

static const wchar_t* SkipProgramName(const wchar_t* commandLine)
{
	const wchar_t* current = commandLine;
	BOOL inQuotes = FALSE;

	while (*current == L' ' || *current == L'\t')
	{
		++current;
	}

	while (*current != L'\0')
	{
		if (*current == L' ' || *current == L'\t')
		{
			if (!inQuotes)
			{
				break;
			}

			++current;
			continue;
		}

		if (*current == L'\\')
		{
			const wchar_t* slashStart = current;
			while (*current == L'\\')
			{
				++current;
			}

			if (*current != L'"')
			{
				continue;
			}

			if (((SIZE_T)(current - slashStart) & 1u) != 0)
			{
				++current;
				continue;
			}
		}

		if (*current == L'"')
		{
			inQuotes = !inQuotes;
			++current;
			continue;
		}

		++current;
	}

	return current;
}

static BOOL BuildChildCommandLine(const wchar_t* targetPath, wchar_t** childCommandLine)
{
	const wchar_t* original = GetCommandLineW();
	const wchar_t* tail = SkipProgramName(original);
	SIZE_T targetLength = wcslen(targetPath);
	SIZE_T tailLength = wcslen(tail);
	SIZE_T totalLength = targetLength + tailLength + 3;
	wchar_t* buffer = (wchar_t*)HeapAlloc(GetProcessHeap(), 0, (totalLength + 1) * sizeof(wchar_t));

	if (buffer == NULL)
	{
		return FALSE;
	}

	buffer[0] = L'"';
	memcpy(buffer + 1, targetPath, targetLength * sizeof(wchar_t));
	buffer[targetLength + 1] = L'"';
	memcpy(buffer + targetLength + 2, tail, (tailLength + 1) * sizeof(wchar_t));

	*childCommandLine = buffer;
	return TRUE;
}

static BOOL IsValidInheritedHandle(HANDLE handle)
{
	return handle != NULL && handle != INVALID_HANDLE_VALUE;
}

static DWORD LaunchTarget(const wchar_t* targetPath)
{
	STARTUPINFOW startupInfo;
	PROCESS_INFORMATION processInformation;
	wchar_t* commandLine = NULL;
	DWORD result = ERROR_GEN_FAILURE;
	BOOL inheritHandles = FALSE;
	DWORD creationFlags = 0;

	ZeroMemory(&startupInfo, sizeof(startupInfo));
	startupInfo.cb = sizeof(startupInfo);
	GetStartupInfoW(&startupInfo);
	ZeroMemory(&processInformation, sizeof(processInformation));

#ifdef PUBLISH_SHIM_CONSOLE
	startupInfo.dwFlags |= STARTF_USESTDHANDLES;
	startupInfo.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
	startupInfo.hStdOutput = GetStdHandle(STD_OUTPUT_HANDLE);
	startupInfo.hStdError = GetStdHandle(STD_ERROR_HANDLE);
	inheritHandles = IsValidInheritedHandle(startupInfo.hStdInput) ||
		IsValidInheritedHandle(startupInfo.hStdOutput) ||
		IsValidInheritedHandle(startupInfo.hStdError);
#else
	creationFlags = CREATE_NO_WINDOW;
#endif

	if (!BuildChildCommandLine(targetPath, &commandLine))
	{
		ShowErrorMessage(L"PublishShim", L"Failed to build the child command line.");
		return ERROR_OUTOFMEMORY;
	}

	if (!CreateProcessW(targetPath, commandLine, NULL, NULL, inheritHandles, creationFlags, NULL, NULL, &startupInfo, &processInformation))
	{
		DWORD error = GetLastError();
		HeapFree(GetProcessHeap(), 0, commandLine);
		if (GetFileAttributesW(targetPath) == INVALID_FILE_ATTRIBUTES)
		{
			ShowErrorMessage(L"PublishShim", L"The target executable does not exist.");
		}
		else
		{
			ShowErrorMessage(L"PublishShim", L"Failed to start the target executable.");
		}

		return error;
	}

	HeapFree(GetProcessHeap(), 0, commandLine);

#ifdef PUBLISH_SHIM_CONSOLE
	WaitForSingleObject(processInformation.hProcess, INFINITE);
	if (!GetExitCodeProcess(processInformation.hProcess, &result))
	{
		result = ERROR_GEN_FAILURE;
	}
#else
	result = 0;
#endif

	CloseHandle(processInformation.hThread);
	CloseHandle(processInformation.hProcess);
	return result;
}

static int RunShim(void)
{
	wchar_t* selfPath = NULL;
	wchar_t* targetRelativePath = NULL;
	wchar_t* executableDirectory = NULL;
	wchar_t* targetPath = NULL;
	DWORD result = ERROR_GEN_FAILURE;

	if (!GetModulePath(&selfPath))
	{
		ShowErrorMessage(L"PublishShim", L"Failed to resolve the shim path.");
		return (int)ERROR_GEN_FAILURE;
	}

	if (!LoadTargetRelativePath(selfPath, &targetRelativePath))
	{
		HeapFree(GetProcessHeap(), 0, selfPath);
		return (int)ERROR_GEN_FAILURE;
	}

	if (!GetExecutableDirectory(selfPath, &executableDirectory))
	{
		ShowErrorMessage(L"PublishShim", L"Failed to resolve the shim directory.");
		goto Cleanup;
	}

	if (!CombinePath(executableDirectory, targetRelativePath, &targetPath))
	{
		ShowErrorMessage(L"PublishShim", L"Failed to allocate the target path.");
		goto Cleanup;
	}

	result = LaunchTarget(targetPath);

Cleanup:
	if (targetPath != NULL)
	{
		HeapFree(GetProcessHeap(), 0, targetPath);
	}

	if (executableDirectory != NULL)
	{
		HeapFree(GetProcessHeap(), 0, executableDirectory);
	}

	if (targetRelativePath != NULL)
	{
		HeapFree(GetProcessHeap(), 0, targetRelativePath);
	}

	if (selfPath != NULL)
	{
		HeapFree(GetProcessHeap(), 0, selfPath);
	}

	return (int)result;
}

#ifdef PUBLISH_SHIM_CONSOLE
int wmain(void)
{
	return RunShim();
}
#else
int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previousInstance, PWSTR commandLine, int showCommand)
{
	(void)instance;
	(void)previousInstance;
	(void)commandLine;
	(void)showCommand;
	return RunShim();
}
#endif
