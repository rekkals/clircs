clircs.registerCommand(
  "scriptdemo",
  ["sdemo"],
  "Demonstrate a sandboxed command, timer, and private storage.",
  context => {
    const runCount = Number(clircs.storage.get("runCount", "0")) + 1;
    clircs.storage.set("runCount", String(runCount));
    const location = context.networkId || "offline";
    clircs.setTimeout(
      () => clircs.print(`timer completed for ${location}`),
      100
    );
    return `script demo run ${runCount}; network ${location}`;
  }
);

clircs.on("join", event => {
  clircs.print(`join event on network ${event.networkId}: ${event.text}`);
});
