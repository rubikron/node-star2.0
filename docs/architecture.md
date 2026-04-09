Ideally this is how the project should be set up (according to Claude) :

src/SwBridge              COM Connection, all SW API calls
src/App                   WPF GUI, wires everything together
src/LlmOrchestrator       LLM API calls, tool dispatch
docs/                     Document project architecture and which APIs are exposed
tests/                    Running any tests that don't require a live SW instance

Feel free to rename LlmOrchestrator to whatever, just make sure you use the dotnet utility for it

Both src/App and src/LlmOrchestrator are dotnet referenced TO src/SwBridge (aka they depend on SwBridge)
