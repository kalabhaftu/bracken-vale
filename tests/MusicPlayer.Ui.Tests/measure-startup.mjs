import {chromium} from "playwright-core";
import assert from "node:assert/strict";
import fs from "node:fs/promises";
import path from "node:path";
import {fileURLToPath} from "node:url";
import {execFileSync} from "node:child_process";
if(process.env.GITHUB_ACTIONS!=="true")throw new Error("Disposable Windows runner required.");
const output=process.env.MUSICPLAYER_TEST_OUTPUT;
await fs.mkdir(output,{recursive:true});
const started=Number(process.env.MUSICPLAYER_STARTED_MS);
let browser,page;
for(let attempt=0;attempt<120;attempt++){
  try{browser=await chromium.connectOverCDP("http://127.0.0.1:9222");break;}catch{await new Promise(r=>setTimeout(r,500));}
}
assert(browser,"WebView did not start.");
for(let attempt=0;attempt<120;attempt++){
  page=browser.contexts().flatMap(c=>c.pages()).find(p=>p.url().startsWith("https://musicplayer.local/"));
  if(page)break;await new Promise(r=>setTimeout(r,500));
}
assert(page,"Music Player navigation did not complete.");
await page.locator('#routeView tr[data-track]').first().waitFor({state:"visible",timeout:120000});
const usableLibraryMs=Date.now()-started;
const measure=()=>JSON.parse(execFileSync("powershell.exe",["-NoLogo","-NoProfile","-File",path.join(path.dirname(fileURLToPath(import.meta.url)),"Measure-Processes.ps1"),"-AppProcessId",process.env.MUSICPLAYER_TEST_APP_PID],{encoding:"utf8"}));
const ready=measure();
const call=(name)=>page.evaluate(async name=>(await import("/scripts/api.js")).command(name,{}),name);
await call("cancelScan");
for(let attempt=0;attempt<120;attempt++){
  if(!(await call("getBootstrap")).scan.active)break;
  await new Promise(r=>setTimeout(r,500));
}
const settled=measure();
await new Promise(r=>setTimeout(r,10000));
const idle=measure();
await page.screenshot({path:`${output}/library.png`,fullPage:true});
await fs.writeFile(`${output}/measurement.json`,JSON.stringify({usableLibraryMs,ready,settled,idle,idleCpuSeconds:Math.max(0,idle.CpuSeconds-settled.CpuSeconds)},null,2));
await browser.close();
