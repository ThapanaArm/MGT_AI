import DataSourceCategoriesPanel from '../components/DataSourceCategoriesPanel';

export default function DataSourceCategoriesPage() {
  return (
    <div className="page">
      <div className="page-header">
        <h1>Setting Category</h1>
        <p>
          Create the Category and Sub Category used to group data sources. Choose them in the
          "Category" fields of the Data source registry when you register or edit a data source.
        </p>
      </div>

      <DataSourceCategoriesPanel />
    </div>
  );
}
