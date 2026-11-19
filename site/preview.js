const views = {
  dashboard: "浅色用量分布：应用流量矩形图与底部连接详情",
  "dashboard-dark": "深色主题下的应用流量矩形图",
  history: "独立的历史记录页，可按日期查看用量"
};
const buttons = document.querySelectorAll("[data-shot]");
for (const button of buttons) {
  button.addEventListener("click", () => {
    const name = button.dataset.shot;
    const image = document.querySelector("#shot");
    image.alt = views[name];
    image.src = "/" + name + ".png";
    document.querySelector("#full").href = image.src;
    for (const item of buttons) item.setAttribute("aria-pressed", String(item === button));
  });
}
