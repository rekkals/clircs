# clircs script demo

This script is bundled but not loaded automatically.

In clircs, run:

```text
/script list
/script load script-demo
/scriptdemo
```

`/scriptdemo` registers a built-in-style command, stores a private run counter, and starts a bounded one-shot timer. The JOIN event handler displays the stable network ID supplied with each event, demonstrating that simultaneous networks remain distinguishable.
