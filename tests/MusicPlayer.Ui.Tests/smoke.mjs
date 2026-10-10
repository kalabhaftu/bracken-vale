import { chromium } from "playwright-core";
import assert from "node:assert/strict";
import fs from "node:fs/promises";
import { execFileSync } from "node:child_process";
import path from "node:path";
import { fileURLToPath } from "node:url";

if(process.env.GITHUB_ACTIONS!=="true")throw new Error("Run only in an isolated GitHub Windows runner.");
const output=process.env.MUSICPLAYER_TEST_OUTPUT||"artifacts/ui-evidence";
await fs.mkdir(output,{recursive:true});
const started=performance.now();
let browser;
for(let attempt=0;attempt<120;attempt++){
  try{browser=await chromium.connectOverCDP("http://127.0.0.1:9222");break;}catch{await new Promise(resolve=>setTimeout(resolve,500));}
}
assert(browser,"WebView2 CDP connection did not become ready");
let page;
for(let attempt=0;attempt<120;attempt++){
  page=browser.contexts().flatMap(context=>context.pages()).find(page=>page.url().startsWith("https://musicplayer.local/"));
  if(page)break;
  await new Promise(resolve=>setTimeout(resolve,500));
}
assert(page,"Embedded Music Player page was not found");
const errors=[];
page.on("pageerror",error=>errors.push(error.message));
const call=(name,payload={})=>page.evaluate(async({name,payload})=>{
  const api=await import("/scripts/api.js");return api.command(name,payload);
},{name,payload});
function metrics(){return JSON.parse(execFileSync("powershell.exe",["-NoLogo","-NoProfile","-File",path.join(path.dirname(fileURLToPath(import.meta.url)),"Measure-Processes.ps1"),"-AppProcessId",process.env.MUSICPLAYER_TEST_APP_PID],{encoding:"utf8",windowsHide:true}));}
let mainWindowHandle;
function nativeWindow(action){
  const args=["-NoLogo","-NoProfile","-File",path.join(path.dirname(fileURLToPath(import.meta.url)),"Control-Window.ps1"),"-AppProcessId",process.env.MUSICPLAYER_TEST_APP_PID,"-Action",action];
  if(mainWindowHandle)args.push("-MainWindowHandle",mainWindowHandle);
  const result=JSON.parse(execFileSync("powershell.exe",args,{encoding:"utf8",windowsHide:true}));
  mainWindowHandle=result.handle;return result;
}
function videoControl(action,name="",value=0){
  return JSON.parse(execFileSync("powershell.exe",["-NoLogo","-NoProfile","-File",path.join(path.dirname(fileURLToPath(import.meta.url)),"Control-Video.ps1"),"-AppProcessId",process.env.MUSICPLAYER_TEST_APP_PID,"-Action",action,"-Name",name,"-Value",String(value)],{encoding:"utf8",windowsHide:true}));
}
async function navigate(view){
  if(view==="Settings"){await page.locator("#topMenuToggle").click();await page.locator('#topMenu [data-view="Settings"]').click();}
  else if(view==="Queue")await page.locator('.player [data-view="Queue"]').click();
  else await page.locator(`#navigation [data-view="${view}"]`).click();
}
async function until(action,message,timeout=120000){
  const deadline=Date.now()+timeout;
  do{if(await action())return;await new Promise(resolve=>setTimeout(resolve,200));}while(Date.now()<deadline);
  throw new Error(message);
}
await until(async()=>!(await page.locator("#routeView").innerText()).includes("Loading your library"),"Initial view did not render");
assert(!(await page.locator("#routeView").innerText()).includes("could not connect"));
const usableViewMs=performance.now()-started;
if(process.argv.includes("--restart")){
  const restored=await call("getBootstrap");
  assert.equal(restored.queueTotal,5,"Queue did not survive restart");
  assert.equal(restored.playing,false,"Restart resumed playback unexpectedly");
  assert.equal(restored.settings.autoLoadLyrics,false,"Settings did not survive restart");
}
await until(async()=>{
  const data=await call("getTracks",{view:"Songs",pageSize:200});
  return data.tracks.filter(track=>track.title.startsWith("MusicPlayerSmoke-")).length>=4;
},"Automatic clean-profile drive discovery did not find the four fixtures",240000);
await call("cancelScan");
await until(async()=>!(await call("getBootstrap")).scan.active,"Scan cancellation did not complete");
await call("updateSettings",{settings:{autoLoadLyrics:false,motionStyle:"Off"}});
await call("setVolume",{volume:0});
await call("setExtensionEnabled",{extension:".mkv",enabled:true,kind:"Video"});
let video;
await until(async()=>{
  video=(await call("getTracks",{view:"Songs",pageSize:200})).tracks.find(track=>track.title==="MusicPlayerVideoSmoke");
  return !!video;
},"Enabled video fixture was not discovered",240000);
await call("cancelScan");
await call("playTrack",{id:video.id,view:"Songs"});
await until(async()=>{const state=await call("getCurrentTrack");return state.playing&&state.isVideo&&state.positionSeconds>=1;},"Installed video playback did not advance");
videoControl("Invoke","Pause");
await until(async()=>!(await call("getCurrentTrack")).playing,"Video pause button failed");
videoControl("Seek","",3);
await until(async()=>Math.abs((await call("getCurrentTrack")).positionSeconds-3)<1,"Video seek slider failed");
videoControl("Speed","1.5×");
videoControl("Subtitle");
videoControl("Invoke","Enter full screen (F11)");
// Fullscreen deliberately hides the transport panel; exercise its documented
// Escape accelerator rather than looking for the collapsed exit button.
videoControl("Escape");
videoControl("Invoke","Play");
await until(async()=>(await call("getCurrentTrack")).playing,"Video play button failed");
videoControl("Invoke","Save a screenshot of the current frame");
videoControl("Close");
await until(async()=>!(await call("getCurrentTrack")).playing,"Closing video did not pause playback");
await page.locator('#navigation [data-view="Songs"]').click();
await until(async()=>await page.locator('#routeView tr[data-track]').count()>=4,"Saved songs did not render");
const fixtureRows=await call("getTracks",{view:"Songs",pageSize:200});
const fixtures=fixtureRows.tracks.filter(track=>track.title.startsWith("MusicPlayerSmoke-")).sort((a,b)=>a.title.localeCompare(b.title));
await page.locator("#globalSearch").fill("MusicPlayerSmoke-A");
await until(async()=> (await call("getBootstrap")).view==="Search","Search navigation failed");
await until(async()=> (await page.locator("#routeView").innerText()).includes("MusicPlayerSmoke-A"),"Search result missing");
await page.locator('#navigation [data-view="Songs"]').click();
await until(async()=>await page.locator('#routeView tr[data-track]').count()>=4,"Songs retained the previous search filter");
await call("playTrack",{id:fixtures[0].id,view:"Songs"});
await until(async()=>(await call("getCurrentTrack")).playing,"Native playback did not start");
await call("playPause");
await until(async()=>!(await call("getCurrentTrack")).playing,"Playback did not pause before queue editing");
await call("clearQueue");
for(const track of [fixtures[1],fixtures[1],fixtures[2],fixtures[3]])await call("addToQueue",{id:track.id});
const queueBefore=await call("getQueue",{pageSize:100});
assert.equal(queueBefore.totalCount,5);
assert.equal(queueBefore.entries[1].id,queueBefore.entries[2].id);
await navigate("Queue");
await until(async()=>await page.locator("[data-queue-drag='1']").count()===1,"Queue did not render");
assert.equal(await page.locator("[data-queue-drag='1']").isEnabled(),true,"Upcoming queue entry cannot be dragged");
await page.locator("[data-queue-index='4']").scrollIntoViewIfNeeded();
const pointerEvents=[];
await page.exposeFunction("recordQueuePointer",event=>pointerEvents.push(event));
await page.evaluate(()=>{
  for(const type of ["pointerdown","pointermove","pointerup","pointercancel","lostpointercapture"])
    document.querySelector("#routeView").addEventListener(type,event=>window.recordQueuePointer({type:event.type,x:event.clientX,y:event.clientY,target:event.target.outerHTML?.slice(0,300),hit:document.elementFromPoint(event.clientX,event.clientY)?.outerHTML?.slice(0,300),dragging:document.body.classList.contains("queue-pointer-dragging"),drop:document.querySelector(".queue-drop-after,.queue-drop-before")?.dataset.queueIndex}),true);
});
const grip=await page.locator("[data-queue-drag='1']").boundingBox();
const target=await page.locator("[data-queue-index='4']").boundingBox();
assert(grip&&target);
await page.mouse.move(grip.x+grip.width/2,grip.y+grip.height/2);
await page.mouse.down();
await page.mouse.move(target.x+target.width/2,target.y+target.height*.75,{steps:15});
await page.screenshot({path:`${output}/queue-drag.png`,fullPage:true});
await page.mouse.up();
await fs.writeFile(`${output}/queue-pointer.json`,JSON.stringify({queueBefore,grip,target,pointerEvents,queueAfter:await call("getQueue",{pageSize:100})},null,2));
await until(async()=> (await call("getQueue",{pageSize:100})).entries[4].id===fixtures[1].id,"Pointer queue reorder did not persist");
await page.locator("#immersiveToggle").click();
await page.locator("#immersiveLyricsToggle").click();
await until(async()=>await page.locator("#immersiveLyricsLines").innerText().then(text=>text.includes("Smoke lyric")),"Immersive saved lyrics did not appear");
await page.locator("#immersiveLyricsClose").click();
await page.locator("#immersiveLyricsToggle").click();
assert.equal(await page.locator("#immersiveLyricsToggle").getAttribute("aria-expanded"),"true");
await page.locator("#immersiveExit").click();
const memorySamples=[metrics()];
for(let index=0;index<15;index++){
  for(const view of ["Songs","Queue","Settings","Home"]){
    await navigate(view);
    await until(async()=>!(await page.locator("#routeView").innerText()).includes("Loading your library"),"Navigation did not settle");
  }
  if(index%5===4)memorySamples.push(metrics());
}
// Compare the last two batches after route caches have warmed up. The bound
// tolerates browser housekeeping while rejecting sustained per-navigation growth.
const navigationMemoryGrowth=memorySamples.at(-1).PrivateBytes-memorySamples.at(-2).PrivateBytes;
assert(navigationMemoryGrowth<32*1024*1024,"Combined native/WebView memory grew more than 32 MiB over the final 20 navigations");
// Exercise native controls while the renderer is suspended. Only the disposable
// runner receives window messages and a real Windows media-key input.
await call("updateSettings",{settings:{minimizeToTray:true}});
await call("playQueueEntry",{index:0});
await until(async()=>(await call("getCurrentTrack")).playing,"Playback did not resume before minimize");
await call("seek",{seconds:88});
const beforeMinimize=metrics();
assert.equal(nativeWindow("Minimize").visible,false,"Tray minimize did not hide the native window");
await new Promise(resolve=>setTimeout(resolve,5000));
const minimized=metrics();
nativeWindow("TrayRestore");
await until(()=>nativeWindow("Inspect").visible,"Tray activation did not restore the window",15000);
await until(async()=>(await call("getCurrentTrack")).queueIndex===1,"Native end-of-song advancement stopped while WebView was minimized");
nativeWindow("TaskbarToggle");
await until(async()=>!(await call("getCurrentTrack")).playing,"Taskbar thumbnail pause did not control playback");
nativeWindow("TaskbarToggle");
await until(async()=>(await call("getCurrentTrack")).playing,"Taskbar thumbnail play did not control playback");
nativeWindow("MediaKey");
await until(async()=>!(await call("getCurrentTrack")).playing,"Windows media key did not control native playback");
await new Promise(resolve=>setTimeout(resolve,500));
assert.equal((await call("getCurrentTrack")).playing,false,"Media key toggled playback twice");
await call("playQueueEntry",{index:0});
await until(async()=>{const state=await call("getCurrentTrack");return state.playing&&state.queueIndex===0;},"Final queue selection did not start");
await call("playPause");
await until(async()=>!(await call("getCurrentTrack")).playing,"Final paused state did not settle");
const idleBefore=metrics();
await new Promise(resolve=>setTimeout(resolve,5000));
const idleAfter=metrics();
await page.screenshot({path:`${output}/final-ui.png`,fullPage:true});
await fs.writeFile(`${output}/results${process.argv.includes("--restart")?"-restart":""}.json`,JSON.stringify({usableViewMs,discoveredFixtures:fixtures.length,installedVideo:{play:true,pause:true,seek:true,speed:true,embeddedSubtitle:true,fullscreen:true,screenshotCommand:true,closePauses:true},queueDragging:true,duplicateQueue:true,immersiveLyrics:true,repeatedNavigation:60,memorySamples,minimizedPlayback:{beforeMinimize,minimized,nativeAdvancement:true,tray:true,taskbar:true,mediaKey:true},pausedIdle:{before:idleBefore,after:idleAfter},errors},null,2));
assert.deepEqual(errors,[],"WebUI raised JavaScript errors");
console.log("Windows WebView UI smoke passed");
await browser.close();
