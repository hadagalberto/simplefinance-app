import { useState } from 'react'
import { Icon as IconifyIcon } from '@/components/Icon'
import { useTranslation } from 'react-i18next'
import {
  Alert,
  Badge,
  Button,
  Card,
  CardBody,
  Dropdown,
  DropdownItem,
  DropdownMenu,
  DropdownToggle,
  Form,
  ListGroup,
  ListGroupItem,
  Modal,
  Placeholder,
} from 'react-bootstrap'

import { useNotificationContext } from '@/shell/toast/useNotificationContext'
import { useAuth } from '@/features/auth/useAuth'
import { ApiError } from '@/lib/apiClient'

import {
  useCategoriesQuery,
  useDeleteCategoryMutation,
  useToggleCategoryHideMutation,
  type CategoryResponse,
} from './api'
import { CategoryDialog } from './CategoryDialog'
import { resolveCategoryIcon } from './icons'

interface CategoryTreeNode {
  parent: CategoryResponse
  children: CategoryResponse[]
}

/**
 * Groups the flat CategoryResponse[] into parent -> children (a flat
 * grouped list, not a literal collapsible tree widget, given the seeded
 * tree's shallow 2-level depth) -- ported unchanged from v1's
 * CategoriesPage.tsx.
 */
function buildTree(categories: CategoryResponse[]): CategoryTreeNode[] {
  const childrenByParentId = new Map<string, CategoryResponse[]>()
  for (const category of categories) {
    if (!category.parentCategoryId) continue
    const siblings = childrenByParentId.get(category.parentCategoryId) ?? []
    siblings.push(category)
    childrenByParentId.set(category.parentCategoryId, siblings)
  }

  return categories
    .filter((category) => !category.parentCategoryId)
    .map((parent) => ({
      parent,
      children: (childrenByParentId.get(parent.id) ?? []).sort((a, b) => a.name.localeCompare(b.name)),
    }))
    .sort((a, b) => a.parent.name.localeCompare(b.parent.name))
}

interface CategoryRowProps {
  category: CategoryResponse
  accessToken: string
  indented?: boolean
  onEdit: (category: CategoryResponse) => void
  onDelete: (category: CategoryResponse) => void
}

function CategoryRow({ category, accessToken, indented = false, onEdit, onDelete }: CategoryRowProps) {
  const { t } = useTranslation(['categories', 'common'])
  const toggleHideMutation = useToggleCategoryHideMutation()
  const { showNotification } = useNotificationContext()
  const color = category.color ?? '#78716C'

  async function handleToggleHide(checked: boolean) {
    try {
      await toggleHideMutation.mutateAsync({ accessToken, id: category.id, isHidden: checked })
    } catch {
      showNotification({ message: t('errors.hide'), variant: 'danger' })
    }
  }

  return (
    <div className={`d-flex align-items-center justify-content-between gap-3 ${indented ? 'ps-5' : ''}`}>
      <div className="d-flex align-items-center gap-3 min-w-0">
        <span
          className="d-flex align-items-center justify-content-center rounded-circle flex-shrink-0"
          style={{ backgroundColor: `${color}1A`, color, width: 32, height: 32 }}
        >
          <IconifyIcon icon={resolveCategoryIcon(category.icon)} />
        </span>
        <div className="min-w-0">
          <p className="fw-medium mb-0 text-truncate">{category.name}</p>
          <p className="text-muted fs-xs mb-0">{t(`types.${category.type}`)}</p>
        </div>
        {category.isSystem && (
          <Badge bg="light" text="dark" className="flex-shrink-0">
            {t('row.system')}
          </Badge>
        )}
      </div>
      <div className="d-flex align-items-center gap-3 flex-shrink-0">
        <Form.Check
          type="switch"
          checked={category.isHidden}
          disabled={toggleHideMutation.isPending}
          onChange={(event) => void handleToggleHide(event.target.checked)}
          aria-label={category.isHidden ? t('row.showCategory') : t('row.hideCategory')}
        />
        <Dropdown align="end">
          <DropdownToggle
            variant="link"
            className="text-muted drop-arrow-none p-0"
            aria-label={t('row.actionsLabel')}
          >
            <IconifyIcon icon="lucide:more-vertical" />
          </DropdownToggle>
          <DropdownMenu>
            <DropdownItem onClick={() => onEdit(category)}>{t('common:actions.edit')}</DropdownItem>
            {!category.isSystem && (
              <DropdownItem className="text-danger" onClick={() => onDelete(category)}>
                {t('common:actions.delete')}
              </DropdownItem>
            )}
          </DropdownMenu>
        </Dropdown>
      </div>
    </div>
  )
}

function GroupSkeleton() {
  return (
    <Card>
      <CardBody className="d-flex flex-column gap-3">
        <div className="d-flex align-items-center gap-3">
          <Placeholder as="span" animation="glow">
            <Placeholder xs={12} style={{ width: 32, height: 32, borderRadius: '50%' }} />
          </Placeholder>
          <Placeholder as="p" animation="glow" className="mb-0">
            <Placeholder xs={8} />
          </Placeholder>
        </div>
        <div className="d-flex align-items-center gap-3 ps-5">
          <Placeholder as="span" animation="glow">
            <Placeholder xs={12} style={{ width: 32, height: 32, borderRadius: '50%' }} />
          </Placeholder>
          <Placeholder as="p" animation="glow" className="mb-0">
            <Placeholder xs={6} />
          </Placeholder>
        </div>
      </CardBody>
    </Card>
  )
}

/**
 * FE-CAT-01's Categories half: Settings > Categories tree (parent ->
 * children), add/edit dialog (CategoryDialog.tsx), hide/unhide, and
 * delete. System categories never offer delete -- the backend's 403 in
 * DeleteCategoryAsync is the real enforcement boundary (T-11-05), this is
 * cosmetic-only. Routed at /settings/categories.
 */
export function CategoriesPage() {
  const { t } = useTranslation(['categories', 'common'])
  const { accessToken } = useAuth()
  const token = accessToken ?? ''
  const categoriesQuery = useCategoriesQuery(token)
  const deleteCategoryMutation = useDeleteCategoryMutation()
  const { showNotification } = useNotificationContext()

  const [dialogOpen, setDialogOpen] = useState(false)
  const [editTarget, setEditTarget] = useState<CategoryResponse | null>(null)
  const [deleteTarget, setDeleteTarget] = useState<CategoryResponse | null>(null)

  function handleAddCategory() {
    setEditTarget(null)
    setDialogOpen(true)
  }

  function handleEditCategory(category: CategoryResponse) {
    setEditTarget(category)
    setDialogOpen(true)
  }

  async function handleDelete() {
    if (!deleteTarget) return

    try {
      await deleteCategoryMutation.mutateAsync({ accessToken: token, id: deleteTarget.id })
      showNotification({ message: t('toasts.deleted'), variant: 'success' })
    } catch (error) {
      if (error instanceof ApiError && error.status === 409) {
        showNotification({ message: t('errors.deleteInUse'), variant: 'danger' })
      } else {
        showNotification({ message: t('errors.delete'), variant: 'danger' })
      }
    } finally {
      setDeleteTarget(null)
    }
  }

  const categories = categoriesQuery.data ?? []
  const tree = buildTree(categories)

  return (
    <div>
      <div className="d-flex flex-wrap align-items-center justify-content-between gap-3 mb-4">
        <h4 className="mb-0">{t('page.title')}</h4>
        <Button variant="primary" onClick={handleAddCategory}>
          <IconifyIcon icon="lucide:plus" className="me-1 align-middle" />
          {t('page.addCategory')}
        </Button>
      </div>

      {categoriesQuery.isError && (
        <Alert variant="danger" className="d-flex align-items-center justify-content-between gap-3">
          <span>{t('errors.load')}</span>
          <Button variant="outline-danger" size="sm" onClick={() => void categoriesQuery.refetch()}>
            {t('common:actions.retry')}
          </Button>
        </Alert>
      )}

      {categoriesQuery.isPending && (
        <div className="d-flex flex-column gap-3">
          <GroupSkeleton />
          <GroupSkeleton />
          <GroupSkeleton />
        </div>
      )}

      {!categoriesQuery.isPending && !categoriesQuery.isError && tree.length === 0 && (
        <Card>
          <CardBody className="text-center py-5">
            <IconifyIcon icon="lucide:tags" className="fs-1 text-muted mb-3" />
            <h5>{t('empty.title')}</h5>
            <p className="text-muted mb-3">{t('empty.description')}</p>
            <Button variant="primary" onClick={handleAddCategory}>
              {t('page.addCategory')}
            </Button>
          </CardBody>
        </Card>
      )}

      {!categoriesQuery.isPending && !categoriesQuery.isError && tree.length > 0 && (
        <div className="d-flex flex-column gap-3">
          {tree.map(({ parent, children }) => (
            <Card key={parent.id}>
              <ListGroup variant="flush">
                <ListGroupItem>
                  <CategoryRow
                    category={parent}
                    accessToken={token}
                    onEdit={handleEditCategory}
                    onDelete={setDeleteTarget}
                  />
                </ListGroupItem>
                {children.map((child) => (
                  <ListGroupItem key={child.id}>
                    <CategoryRow
                      category={child}
                      accessToken={token}
                      indented
                      onEdit={handleEditCategory}
                      onDelete={setDeleteTarget}
                    />
                  </ListGroupItem>
                ))}
              </ListGroup>
            </Card>
          ))}
        </div>
      )}

      <Modal show={!!deleteTarget} onHide={() => setDeleteTarget(null)} centered>
        <Modal.Header closeButton>
          <Modal.Title>{t('deleteDialog.title')}</Modal.Title>
        </Modal.Header>
        <Modal.Body>{t('deleteDialog.body', { name: deleteTarget?.name ?? '' })}</Modal.Body>
        <Modal.Footer>
          <Button variant="light" onClick={() => setDeleteTarget(null)}>
            {t('common:actions.cancel')}
          </Button>
          <Button variant="danger" onClick={() => void handleDelete()} disabled={deleteCategoryMutation.isPending}>
            {t('deleteDialog.confirm')}
          </Button>
        </Modal.Footer>
      </Modal>

      <CategoryDialog
        show={dialogOpen}
        onHide={() => setDialogOpen(false)}
        categories={categories}
        editTarget={editTarget}
        accessToken={token}
      />
    </div>
  )
}
