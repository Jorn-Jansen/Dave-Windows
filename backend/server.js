import http from "node:http";
import Anthropic from "@anthropic-ai/sdk";
import Groq from "groq-sdk";

const PORT = Number(process.env.PORT) || 3000;
const APP_TOKEN = process.env.APP_TOKEN;
const PROVIDER = (process.env.AI_PROVIDER || "groq").toLowerCase();
const LOCATION = process.env.DRIVER_LOCATION || "the Netherlands";

if (!APP_TOKEN) {
  console.error("APP_TOKEN is not set. Copy .env.example to .env and fill it in.");
  process.exit(1);
}
if (!["groq", "claude"].includes(PROVIDER)) {
  console.error(`Unknown AI_PROVIDER "${PROVIDER}". Use "groq" or "claude".`);
  process.exit(1);
}

const SYSTEM = `You are Dave, a voice assistant built into the driver's car, a 2013 Mercedes-Benz CLA 200 AMG Line.
Reply in whatever language the driver speaks to you.
Everything you say is read aloud by text-to-speech while they drive, so:
- Answer in one to three short spoken sentences unless they ask for more detail.
- Use plain speech only: no markdown, lists, emojis, links, or symbols that sound strange read aloud.
- Say numbers, times, and units the way a person would say them out loud.
- If something would need them to read a screen or take their attention off the road, suggest doing it once they're parked.
If you can search the web, use it for anything current, like news, weather, traffic, opening hours, scores, or prices.`;

// Short-term memory so follow-up questions work ("and what about tomorrow?").
// Forgotten after 10 minutes of silence.
const MEMORY_MS = 10 * 60 * 1000;
const MAX_TURNS = 10;
let history = [];
let lastActivity = 0;

// --- Groq (default): free tier, very fast ---

const groq = PROVIDER === "groq" ? new Groq() : null;
const GROQ_MODEL = process.env.GROQ_MODEL || "openai/gpt-oss-120b";
let groqSearch = process.env.GROQ_SEARCH !== "false";

async function askGroq(messages) {
  const request = {
    model: GROQ_MODEL,
    reasoning_effort: "low",
    messages: [{ role: "system", content: SYSTEM }, ...messages],
  };
  if (groqSearch) {
    try {
      const completion = await groq.chat.completions.create({ ...request, tools: [{ type: "browser_search" }] });
      return completion.choices[0]?.message?.content;
    } catch (error) {
      if (!(error instanceof Groq.BadRequestError || error instanceof Groq.PermissionDeniedError)) throw error;
      // Web search isn't available on this account/plan; carry on without it.
      console.warn("Groq web search unavailable, continuing without it:", error.message);
      groqSearch = false;
    }
  }
  const completion = await groq.chat.completions.create(request);
  return completion.choices[0]?.message?.content;
}

// --- Claude: paid, smarter, with web search ---

const claude = PROVIDER === "claude" ? new Anthropic() : null;

async function askClaude(messages) {
  messages = [...messages];
  let response;
  // Web search can pause a long turn; resume it a few times before giving up.
  for (let i = 0; i < 3; i++) {
    response = await claude.beta.messages.create({
      model: "claude-opus-5",
      max_tokens: 4000,
      output_config: { effort: "low" }, // fast replies matter more than deep reasoning while driving
      system: SYSTEM,
      tools: [{ type: "web_search_20260209", name: "web_search", max_uses: 3 }],
      betas: ["server-side-fallback-2026-07-01"],
      fallbacks: "default",
      messages,
    });
    if (response.stop_reason !== "pause_turn") break;
    messages.push({ role: "assistant", content: response.content });
  }
  if (response.stop_reason === "refusal") return "Sorry, I can't help with that one.";
  return response.content
    .filter((block) => block.type === "text")
    .map((block) => block.text)
    .join("");
}

function timeContext() {
  const date = new Date();
  const zone = Intl.DateTimeFormat().resolvedOptions().timeZone;
  const local = date.toLocaleString("en-GB", { weekday: "long", day: "numeric", month: "long", year: "numeric", hour: "2-digit", minute: "2-digit" });
  const offset = -date.getTimezoneOffset() / 60;
  return `Right now it is ${local} for the driver (${zone}, UTC${offset >= 0 ? "+" : ""}${offset}), which is ${date.toISOString().slice(11, 16)} UTC. This is the driver's local time; don't adjust it.`;
}

async function ask(text) {
  if (Date.now() - lastActivity > MEMORY_MS) history = [];
  lastActivity = Date.now();

  // Tell the AI where and when it is, so it doesn't have to search for that (and gets units right).
  const context = `[${timeContext()} Driver's location: ${LOCATION}. Use the units and currency of that country, but always reply in the same language as the question below.]`;
  const messages = [...history, { role: "user", content: `${context}\n\n${text}` }];
  const answer = PROVIDER === "claude" ? await askClaude(messages) : await askGroq(messages);
  const reply = answer?.trim() || "Sorry, I didn't get an answer for that.";

  history.push({ role: "user", content: text }, { role: "assistant", content: reply });
  history = history.slice(-MAX_TURNS * 2);
  return reply;
}

function spokenError(error) {
  if (error instanceof Anthropic.AuthenticationError || error instanceof Groq.AuthenticationError)
    return "My API key isn't working. Check the backend settings.";
  if (error instanceof Anthropic.RateLimitError || error instanceof Groq.RateLimitError)
    return "I've hit my usage limit. Try again in a moment.";
  if (error instanceof Anthropic.APIConnectionError || error instanceof Groq.APIConnectionError)
    return "I can't reach the AI right now. Check the internet connection.";
  return "Sorry, something went wrong reaching the AI.";
}

function send(res, status, body) {
  res.writeHead(status, { "Content-Type": "application/json" });
  res.end(JSON.stringify(body));
}

const server = http.createServer(async (req, res) => {
  if (req.method === "GET" && req.url === "/health") return send(res, 200, { ok: true, provider: PROVIDER });
  if (req.method !== "POST" || req.url !== "/ask") return send(res, 404, { error: "Not found" });
  if (req.headers.authorization !== `Bearer ${APP_TOKEN}`) return send(res, 401, { error: "Unauthorized" });

  let raw = "";
  for await (const chunk of req) raw += chunk;

  let text;
  try {
    text = JSON.parse(raw).text?.trim();
  } catch {
    return send(res, 400, { error: "Body must be JSON like {\"text\": \"...\"}" });
  }
  if (!text) return send(res, 400, { error: "Missing text" });

  console.log(`> ${text}`);
  const started = Date.now();
  try {
    const reply = await ask(text);
    console.log(`< ${reply}  (${Date.now() - started} ms)`);
    send(res, 200, { reply });
  } catch (error) {
    console.error(error);
    send(res, 200, { reply: spokenError(error) });
  }
});

server.on("error", (error) => {
  if (error.code !== "EADDRINUSE") throw error;
  console.error(`Port ${PORT} is already in use. Dave is probably already running in another window; close that one first.`);
  process.exit(1);
});

server.listen(PORT, () => console.log(`Dave backend (${PROVIDER}) listening on http://localhost:${PORT}`));
