# LLM API MD FILE
- Hosted on vercel (Claude sonnet4.6 as of now)

  - has access to the tools querying to a pinecone vector DB (containing the SDS documentation), and web search if proper result is not pulled from database




## Workflow (Input Message to Output message)

### Create(part/design) Flow

(incoming POST Request)
1. Identify if part is available
2. Make query to V-DB for proper VBA command
3. If not found, do a web search for the VBA command (have to make prompt for it)
4. Macro command generator (VBA)
5. verify if VBA macro is in the proper format
(return just code to the client)
