import { AppstoreOutlined, PlusOutlined, ShopOutlined } from "@ant-design/icons";
import { App as AntApp, Layout, Menu, Segmented, Space, Typography } from "antd";
import { Link, Route, Routes, useLocation } from "react-router";
import type { Role } from "./api";
import { Browse } from "./pages/Browse";
import { CategoriesAdmin } from "./pages/CategoriesAdmin";
import { ProductEditor } from "./pages/ProductEditor";
import { ProductPage } from "./pages/ProductPage";
import { useSession } from "./session";

export function App() {
  const { isEditor } = useSession();
  const location = useLocation();
  const items = [
    { key: "/", icon: <ShopOutlined />, label: <Link to="/">Catalog</Link> },
    ...(isEditor
      ? [
          { key: "/products/new", icon: <PlusOutlined />, label: <Link to="/products/new">New product</Link> },
          { key: "/categories", icon: <AppstoreOutlined />, label: <Link to="/categories">Categories</Link> },
        ]
      : []),
  ];

  return (
    <Layout className="shell">
      <Layout.Header className="header">
        <Typography.Title level={4} className="logo">
          <Link to="/">Catalog</Link>
        </Typography.Title>
        <Menu theme="dark" mode="horizontal" selectedKeys={[location.pathname]} items={items} className="menu" />
        <RoleSwitcher />
      </Layout.Header>
      <Layout.Content className="content">
        <Routes>
          <Route path="/" element={<Browse />} />
          <Route path="/products/new" element={<ProductEditor />} />
          <Route path="/products/:id" element={<ProductPage />} />
          <Route path="/products/:id/edit" element={<ProductEditor />} />
          <Route path="/categories" element={<CategoriesAdmin />} />
        </Routes>
      </Layout.Content>
      <Layout.Footer className="footer">LinqToRest catalog sample — React + Ant Design</Layout.Footer>
    </Layout>
  );
}

/** Anonymous, viewer or editor: switching re-runs the queries, so drafts appear and disappear. */
function RoleSwitcher() {
  const { role, signIn } = useSession();
  const { message } = AntApp.useApp();
  return (
    <Space>
      <Typography.Text className="signed-in">Signed in as</Typography.Text>
      <Segmented<string>
        value={role ?? "anonymous"}
        options={["anonymous", "viewer", "editor"]}
        onChange={(value) => {
          const next = value === "anonymous" ? undefined : (value as Role);
          signIn(next).catch((reason: unknown) => {
            void message.error(reason instanceof Error ? reason.message : String(reason));
          });
        }}
      />
    </Space>
  );
}
